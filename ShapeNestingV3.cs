using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace CodexAutoCADBridge
{
    internal static class ShapeNestingV3Workflow
    {
        public static void Run(Document doc)
        {
            if (doc == null)
            {
                return;
            }

            Editor editor = doc.Editor;
            try
            {
                ShapeNestingInput input = ShapeExtractor.Extract(doc);
                editor.WriteMessage("\nDetected board " + input.BoardWidth.ToString("0.###", CultureInfo.InvariantCulture) + " x " + input.BoardHeight.ToString("0.###", CultureInfo.InvariantCulture) + ", part types: " + input.PartTypes.Count.ToString(CultureInfo.InvariantCulture) + ".");

                PromptPointOptions pointOptions = new PromptPointOptions("\n指定 V3 排料结果绘制原点");
                PromptPointResult pointResult = editor.GetPoint(pointOptions);
                if (pointResult.Status != PromptStatus.OK)
                {
                    return;
                }

                editor.WriteMessage("\n开始计算 V3 排料...");
                bool drewBest = false;
                ShapeNestingResult result = ShapeNestingV3Solver.Solve(input, delegate (string message)
                {
                    editor.WriteMessage(message);
                }, delegate (ShapeNestingResult bestResult)
                {
                    ShapeNestingValidator.Validate(bestResult);
                    ShapeNestingDrawer.Redraw(doc, bestResult, pointResult.Value);
                    drewBest = true;
                });
                ShapeNestingValidator.Validate(result);
                editor.WriteMessage("\nV3 排料结果：" + ShapeNestingV3Solver.Format(result) + ".");
                if (!drewBest)
                {
                    ShapeNestingDrawer.Redraw(doc, result, pointResult.Value);
                }
            }
            catch (System.Exception ex)
            {
                if (ex is OperationCanceledException)
                {
                    editor.WriteMessage("\nCDX_NEST_GROUP_V3 已取消。");
                    return;
                }
                editor.WriteMessage("\nCDX_NEST_GROUP_V3 失败：" + ex.Message);
            }
        }
    }

    internal static class ShapeNestingV3Solver
    {
        private const int PopulationSize = 10;
        private const int EliteCount = 3;
        private const int Generations = 40;
        private const int CandidateLimit = 360;
        private const int DenseCandidateLimit = 1200;
        private const double Gap = 2.0;
        private static readonly int[] Rotations = new int[] { 0, 90, 180, 270, 45, 135, 225, 315 };
        private static readonly int[] SeedRotations = new int[] { 0, 90, 180, 270 };
        private static DateTime _nextUiPump;

        public static ShapeNestingResult Solve(ShapeNestingInput input, Action<string> progress, Action<ShapeNestingResult> bestUpdated)
        {
            DateTime started = DateTime.UtcNow;
            _nextUiPump = started;
            int seed = unchecked(Environment.TickCount ^ (int)DateTime.UtcNow.Ticks);
            Random random = new Random(seed);

            if (progress != null)
            {
                progress("\n" + CodexBridgeVersion.Version + " V3：参考 SVGnest/libnest2d，单件遗传算法，优先填已有板，轮廓接触候选点。");
                progress("\nV3 零件排序：" + FormatPartOrder(input) + "。");
                progress("\nV3 随机种子：" + seed.ToString(CultureInfo.InvariantCulture) + "。");
            }

            List<V3PartGene> baseGenes = BuildBaseGenes(input);
            List<V3Chromosome> population = CreatePopulation(input, baseGenes, random);
            ShapeNestingResult best = null;
            V3Score bestScore = null;

            V3Chromosome quickSeed = new V3Chromosome();
            quickSeed.Genes = CloneGenes(baseGenes);
            DateTime quickStarted = DateTime.UtcNow;
            quickSeed.Result = PlaceChromosome(input, quickSeed, true);
            if (quickSeed.Result != null && IsValid(quickSeed.Result))
            {
                quickSeed.Result.Generation = 0;
                quickSeed.Result.Individual = 0;
                quickSeed.Score = Score(quickSeed.Result);
                best = quickSeed.Result;
                bestScore = quickSeed.Score;
                if (progress != null)
                {
                    progress("\nV3 快速种子：" + Format(best) +
                        "，本次耗时 " + (DateTime.UtcNow - quickStarted).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " 秒" +
                        "，总耗时 " + (DateTime.UtcNow - started).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " 秒。");
                }
                NotifyBest(bestUpdated, best, progress);
            }
            else if (progress != null)
            {
                progress("\nV3 快速种子失败，本次耗时 " + (DateTime.UtcNow - quickStarted).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " 秒，继续进入遗传计算。");
            }

            for (int generation = 0; generation <= Generations; generation++)
            {
                DateTime generationStart = DateTime.UtcNow;
                int valid = 0;
                for (int i = 0; i < population.Count; i++)
                {
                    PumpUi(false);
                    DateTime individualStart = DateTime.UtcNow;
                    V3Chromosome chromosome = population[i];
                    string individualText = null;
                    if (chromosome.Result == null)
                    {
                        ShapeNestingResult candidate = PlaceChromosome(input, chromosome, true);
                        if (candidate == null)
                        {
                            chromosome.Result = null;
                            chromosome.Score = null;
                            individualText = "跳过：未能放完所有零件";
                        }
                        else
                        {
                            string validationMessage;
                            if (IsValid(candidate, out validationMessage))
                            {
                                chromosome.Result = candidate;
                                chromosome.Result.Generation = generation;
                                chromosome.Result.Individual = i + 1;
                                chromosome.Score = Score(chromosome.Result);
                                individualText = Format(chromosome.Result);
                            }
                            else
                            {
                                chromosome.Result = null;
                                chromosome.Score = null;
                                individualText = "跳过：校验失败：" + validationMessage;
                            }
                        }
                    }
                    else
                    {
                        individualText = Format(chromosome.Result);
                    }

                    if (chromosome.Result != null)
                    {
                        valid++;
                        if (best == null || CompareScore(chromosome.Score, bestScore) < 0)
                        {
                            best = chromosome.Result;
                            bestScore = chromosome.Score;
                            NotifyBest(bestUpdated, best, progress);
                        }
                    }

                    if (progress != null)
                    {
                        progress("\nV3 第 " + generation.ToString(CultureInfo.InvariantCulture) +
                            " 代 个体 " + (i + 1).ToString(CultureInfo.InvariantCulture) + "/" + population.Count.ToString(CultureInfo.InvariantCulture) +
                            "：" + individualText +
                            "，本个体耗时 " + (DateTime.UtcNow - individualStart).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " 秒" +
                            "，总耗时 " + (DateTime.UtcNow - started).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " 秒");
                    }
                }

                population.Sort(delegate (V3Chromosome a, V3Chromosome b)
                {
                    return CompareScore(a.Score, b.Score);
                });

                if (progress != null)
                {
                    progress("\nV3 第 " + generation.ToString(CultureInfo.InvariantCulture) +
                        " 代完成：有效 " + valid.ToString(CultureInfo.InvariantCulture) + "/" + population.Count.ToString(CultureInfo.InvariantCulture) +
                        "，当前最优 " + (population.Count > 0 && population[0].Result != null ? Format(population[0].Result) : "无") +
                        "，全局最优 " + (best == null ? "无" : Format(best)) +
                        "，本代耗时 " + (DateTime.UtcNow - generationStart).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " 秒" +
                        "，总耗时 " + (DateTime.UtcNow - started).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " 秒");
                    PumpUi(true);
                }

                if (generation == Generations)
                {
                    break;
                }
                population = NextGeneration(input, population, random);
            }

            if (best == null)
            {
                throw new InvalidOperationException("V3 没有找到完整排料结果。");
            }
            if (progress != null)
            {
                progress("\nV3 最终选择：" + Format(best));
                PumpUi(true);
            }
            return best;
        }

        public static string Format(ShapeNestingResult result)
        {
            return result.BoardCount.ToString(CultureInfo.InvariantCulture) +
                " 张板，已用板长 " + (result.Utilization * 100.0).ToString("0.00", CultureInfo.InvariantCulture) +
                "%，最后右余 " + result.MinRightRemnant.ToString("0.###", CultureInfo.InvariantCulture) +
                "，总右余 " + result.TotalRightRemnant.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static List<V3PartGene> BuildBaseGenes(ShapeNestingInput input)
        {
            List<V3PartGene> genes = new List<V3PartGene>();
            for (int typeIndex = 0; typeIndex < input.PartTypes.Count; typeIndex++)
            {
                ShapePartType type = input.PartTypes[typeIndex];
                for (int n = 1; n <= type.Quantity; n++)
                {
                    genes.Add(new V3PartGene
                    {
                        TypeIndex = typeIndex,
                        Number = n,
                        Rotation = BestDefaultRotation(type.Polygon)
                    });
                }
            }
            SortGenesLargeFirst(input, genes);
            return genes;
        }

        private static List<V3Chromosome> CreatePopulation(ShapeNestingInput input, List<V3PartGene> baseGenes, Random random)
        {
            List<V3Chromosome> population = new List<V3Chromosome>();
            V3Chromosome seed = new V3Chromosome();
            seed.Genes = CloneGenes(baseGenes);
            population.Add(seed);

            while (population.Count < PopulationSize)
            {
                V3Chromosome c = new V3Chromosome();
                c.Genes = CloneGenes(baseGenes);
                Mutate(input, c, random, population.Count == 1 ? 0.08 : 0.22);
                population.Add(c);
            }
            return population;
        }

        private static List<V3Chromosome> NextGeneration(ShapeNestingInput input, List<V3Chromosome> population, Random random)
        {
            List<V3Chromosome> next = new List<V3Chromosome>();
            int elites = Math.Min(EliteCount, population.Count);
            for (int i = 0; i < elites; i++)
            {
                next.Add(population[i].CloneWithResult());
            }
            while (next.Count < PopulationSize)
            {
                V3Chromosome parent = population[random.Next(Math.Max(1, Math.Min(population.Count, elites + 5)))];
                V3Chromosome child = parent.CloneGenesOnly();
                Mutate(input, child, random, 0.26);
                next.Add(child);
            }
            return next;
        }

        private static ShapeNestingResult PlaceChromosome(ShapeNestingInput input, V3Chromosome chromosome, bool quick)
        {
            ShapeNestingResult result = new ShapeNestingResult();
            result.Input = input;
            result.TotalArea = TotalPartArea(input);
            List<List<ShapePlacement>> boards = new List<List<ShapePlacement>>();

            foreach (V3PartGene gene in chromosome.Genes)
            {
                PumpUi(false);
                ShapePlacement placement = PlaceGene(input, boards, gene, false, quick);
                if (placement == null)
                {
                    boards.Add(new List<ShapePlacement>());
                    placement = PlaceGeneOnBoard(input, boards, boards.Count - 1, gene, false, quick);
                }
                if (placement == null)
                {
                    return null;
                }
                boards[placement.BoardIndex].Add(placement);
                result.Placements.Add(placement);
                if (!quick)
                {
                    CompressPlacedPart(input, boards, placement);
                }
            }

            if (!quick)
            {
                RepackLastBoardToEarlierBoards(input, result, boards);
            }
            CompactBoards(boards);
            FinalizeResult(input, result, boards);
            return result;
        }

        private static ShapePlacement PlaceGene(ShapeNestingInput input, List<List<ShapePlacement>> boards, V3PartGene gene, bool dense, bool quick)
        {
            for (int boardIndex = 0; boardIndex < boards.Count; boardIndex++)
            {
                ShapePlacement placement = PlaceGeneOnBoard(input, boards, boardIndex, gene, dense, quick);
                if (placement != null)
                {
                    return placement;
                }
            }
            return null;
        }

        private static ShapePlacement PlaceGeneOnBoard(ShapeNestingInput input, List<List<ShapePlacement>> boards, int boardIndex, V3PartGene gene, bool dense, bool quick)
        {
            ShapePartType type = input.PartTypes[gene.TypeIndex];
            List<int> rotations = RotationChoices(gene.Rotation, quick);
            ShapePlacement best = null;
            double bestScore = double.PositiveInfinity;

            foreach (int rotation in rotations)
            {
                ShapePolygon polygon = type.Polygon.RotateNormalize(rotation);
                List<ShapePoint> candidates = CandidatePoints(input, boards[boardIndex], polygon, dense, quick);
                foreach (ShapePoint candidate in candidates)
                {
                    ShapeBounds bounds = TranslateBounds(polygon.Bounds(), candidate.X, candidate.Y);
                    if (!FitsBoard(input, bounds))
                    {
                        continue;
                    }
                    if (HitsAny(polygon, candidate.X, candidate.Y, boards[boardIndex]))
                    {
                        continue;
                    }
                    ShapePlacement placement = MakePlacement(input, gene, polygon, boardIndex, candidate.X, candidate.Y, rotation);
                    double score = PlacementScore(input, boards[boardIndex], placement, dense);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = placement;
                    }
                }
            }
            return best;
        }

        private static void CompressPlacedPart(ShapeNestingInput input, List<List<ShapePlacement>> boards, ShapePlacement placement)
        {
            List<ShapePlacement> board = boards[placement.BoardIndex];
            board.Remove(placement);
            bool moved = true;
            int guard = 0;
            while (moved && guard++ < 8)
            {
                moved = false;
                ShapePlacement left = ShiftUntilBlocked(input, board, placement, -1.0, 0.0);
                if (left.X < placement.X - 1e-6)
                {
                    placement = left;
                    moved = true;
                }
                ShapePlacement down = ShiftUntilBlocked(input, board, placement, 0.0, -1.0);
                if (down.Y < placement.Y - 1e-6)
                {
                    placement = down;
                    moved = true;
                }
            }
            board.Add(placement);
        }

        private static ShapePlacement ShiftUntilBlocked(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement placement, double dx, double dy)
        {
            double step = 64.0;
            ShapePlacement best = placement;
            while (step >= 0.5)
            {
                bool moved = false;
                while (true)
                {
                    ShapePlacement candidate = ClonePlacementAt(best, best.X + dx * step, best.Y + dy * step);
                    if (!FitsBoard(input, candidate.Bounds) || HitsAny(candidate.PlacedPolygon.Translate(-candidate.X, -candidate.Y), candidate.X, candidate.Y, board))
                    {
                        break;
                    }
                    best = candidate;
                    moved = true;
                }
                if (!moved)
                {
                    step *= 0.5;
                }
            }
            return best;
        }

        private static void RepackLastBoardToEarlierBoards(ShapeNestingInput input, ShapeNestingResult result, List<List<ShapePlacement>> boards)
        {
            bool movedAny = true;
            int pass = 0;
            while (movedAny && pass++ < 2)
            {
                movedAny = false;
                for (int sourceBoard = boards.Count - 1; sourceBoard >= 1; sourceBoard--)
                {
                    List<ShapePlacement> order = new List<ShapePlacement>(boards[sourceBoard]);
                    order.Sort(delegate (ShapePlacement a, ShapePlacement b)
                    {
                        int cmp = b.Bounds.MaxX.CompareTo(a.Bounds.MaxX);
                        if (cmp != 0)
                        {
                            return cmp;
                        }
                        return b.Bounds.MaxY.CompareTo(a.Bounds.MaxY);
                    });

                    foreach (ShapePlacement moving in order)
                    {
                        PumpUi(false);
                        if (!boards[sourceBoard].Contains(moving))
                        {
                            continue;
                        }
                        boards[sourceBoard].Remove(moving);
                        result.Placements.Remove(moving);
                        V3PartGene gene = new V3PartGene { TypeIndex = moving.Part.TypeIndex, Number = moving.Part.Number, Rotation = moving.Rotation };
                        ShapePlacement newPlacement = null;
                        for (int target = 0; target < sourceBoard && newPlacement == null; target++)
                        {
                        newPlacement = PlaceGeneOnBoard(input, boards, target, gene, true, false);
                        }
                        if (newPlacement != null)
                        {
                            boards[newPlacement.BoardIndex].Add(newPlacement);
                            result.Placements.Add(newPlacement);
                            CompressPlacedPart(input, boards, newPlacement);
                            movedAny = true;
                        }
                        else
                        {
                            boards[sourceBoard].Add(moving);
                            result.Placements.Add(moving);
                        }
                    }
                    RemoveEmptyTrailingBoards(boards);
                }
            }
        }

        private static List<ShapePoint> CandidatePoints(ShapeNestingInput input, List<ShapePlacement> board, ShapePolygon moving, bool dense, bool quick)
        {
            List<ShapePoint> points = new List<ShapePoint>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            ShapeBounds mb = moving.Bounds();
            AddCandidate(points, seen, 0.0, 0.0);
            double usedX = UsedX(board);

            foreach (ShapePlacement existing in board)
            {
                ShapeBounds b = existing.Bounds;
                AddCandidate(points, seen, b.MaxX + Gap, b.MinY);
                AddCandidate(points, seen, b.MinX, b.MaxY + Gap);
                AddCandidate(points, seen, Math.Max(0.0, b.MaxX - mb.Width), b.MinY);
                AddCandidate(points, seen, b.MinX, Math.Max(0.0, b.MaxY - mb.Height));
                if (!quick)
                {
                    AddCandidate(points, seen, Math.Max(0.0, b.MinX - mb.Width - Gap), b.MinY);
                    AddCandidate(points, seen, b.MinX, Math.Max(0.0, b.MinY - mb.Height - Gap));
                    AddContactCandidates(points, seen, existing.PlacedPolygon, moving);
                }
            }

            if (board.Count > 0)
            {
                double xLimit = quick ? Math.Min(input.BoardWidth, usedX + mb.Width * 1.2) : (dense ? input.BoardWidth : Math.Min(input.BoardWidth, Math.Max(usedX + mb.Width * 1.8, usedX + 650.0)));
                double xStep = Math.Max(quick ? 28.0 : (dense ? 10.0 : 22.0), mb.Width / (quick ? 5.0 : (dense ? 12.0 : 6.0)));
                double yStep = Math.Max(quick ? 28.0 : (dense ? 10.0 : 22.0), mb.Height / (quick ? 5.0 : (dense ? 12.0 : 6.0)));
                for (double x = 0.0; x + mb.Width <= xLimit + 1e-6; x += xStep)
                {
                    for (double y = 0.0; y + mb.Height <= input.BoardHeight + 1e-6; y += yStep)
                    {
                        AddCandidate(points, seen, x, y);
                    }
                }

                if (!quick && dense)
                {
                    for (double x = Math.Max(0.0, usedX - Math.Max(300.0, mb.Width * 1.2)); x + mb.Width <= input.BoardWidth + 1e-6; x += Math.Max(8.0, mb.Width / 14.0))
                    {
                        for (double y = 0.0; y + mb.Height <= input.BoardHeight + 1e-6; y += Math.Max(8.0, mb.Height / 14.0))
                        {
                            AddCandidate(points, seen, x, y);
                        }
                    }
                }
            }
            return SelectCandidates(points, quick ? Math.Min(180, CandidateLimit / 3) : (dense ? DenseCandidateLimit : CandidateLimit));
        }

        private static void AddContactCandidates(List<ShapePoint> points, Dictionary<string, bool> seen, ShapePolygon placed, ShapePolygon moving)
        {
            List<ShapePoint> fixedPoints = AnchorPoints(placed);
            List<ShapePoint> movingPoints = AnchorPoints(moving);
            foreach (ShapePoint fp in fixedPoints)
            {
                foreach (ShapePoint mp in movingPoints)
                {
                    AddCandidate(points, seen, fp.X - mp.X + Gap, fp.Y - mp.Y);
                    AddCandidate(points, seen, fp.X - mp.X - Gap, fp.Y - mp.Y);
                    AddCandidate(points, seen, fp.X - mp.X, fp.Y - mp.Y + Gap);
                    AddCandidate(points, seen, fp.X - mp.X, fp.Y - mp.Y - Gap);
                    AddCandidate(points, seen, fp.X - mp.X, fp.Y - mp.Y);
                }
            }
        }

        private static double PlacementScore(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement placement, bool dense)
        {
            double usedX = UsedX(board);
            double addedX = Math.Max(0.0, placement.Bounds.MaxX - usedX);
            double contact = 0.0;
            double near = 0.0;
            foreach (ShapePlacement existing in board)
            {
                contact += BoundsContact(placement.Bounds, existing.Bounds, 5.0);
                near += ShapeNearReward(placement.PlacedPolygon, existing.PlacedPolygon, 55.0);
            }
            double insideUsedReward = board.Count > 0 && placement.Bounds.MaxX <= usedX + 1e-6 ? input.BoardWidth * 2500000.0 : 0.0;
            return addedX * 90000000.0 +
                placement.Bounds.MaxX * 4000.0 +
                placement.Bounds.MaxY * 1200.0 +
                placement.Bounds.MinY * 25.0 -
                contact * 260000.0 -
                near * 500000.0 -
                insideUsedReward;
        }

        private static ShapePlacement MakePlacement(ShapeNestingInput input, V3PartGene gene, ShapePolygon local, int boardIndex, double x, double y, int rotation)
        {
            ShapePartType type = input.PartTypes[gene.TypeIndex];
            ShapePolygon placed = local.Translate(x, y);
            ShapePartInstance part = new ShapePartInstance
            {
                TypeIndex = gene.TypeIndex,
                Number = gene.Number,
                Name = type.Name,
                Polygon = type.Polygon,
                ColorIndex = type.ColorIndex
            };
            return new ShapePlacement
            {
                Part = part,
                BoardIndex = boardIndex,
                X = x,
                Y = y,
                Rotation = rotation,
                PlacedPolygon = placed,
                Bounds = placed.Bounds()
            };
        }

        private static ShapePlacement ClonePlacementAt(ShapePlacement placement, double x, double y)
        {
            ShapePolygon local = placement.PlacedPolygon.Translate(-placement.X, -placement.Y);
            ShapePolygon placed = local.Translate(x, y);
            return new ShapePlacement
            {
                Part = placement.Part,
                BoardIndex = placement.BoardIndex,
                X = x,
                Y = y,
                Rotation = placement.Rotation,
                PlacedPolygon = placed,
                Bounds = placed.Bounds()
            };
        }

        private static bool HitsAny(ShapePolygon moving, double x, double y, List<ShapePlacement> board)
        {
            ShapePolygon candidate = moving.Translate(x, y);
            ShapeBounds cb = candidate.Bounds();
            foreach (ShapePlacement existing in board)
            {
                if (ShapeCollision.BoundsOverlap(cb, existing.Bounds) && ShapeCollision.Intersects(candidate, cb, existing.PlacedPolygon, existing.Bounds))
                {
                    return true;
                }
            }
            return false;
        }

        private static void FinalizeResult(ShapeNestingInput input, ShapeNestingResult result, List<List<ShapePlacement>> boards)
        {
            int boardCount = 0;
            double totalRight = 0.0;
            double lastUsedX = 0.0;
            for (int i = 0; i < boards.Count; i++)
            {
                if (boards[i].Count == 0)
                {
                    continue;
                }
                boardCount++;
                double usedX = UsedX(boards[i]);
                lastUsedX = usedX;
                totalRight += Math.Max(0.0, input.BoardWidth - usedX);
            }
            result.BoardCount = Math.Max(1, boardCount);
            result.MinRightRemnant = Math.Max(0.0, input.BoardWidth - lastUsedX);
            result.TotalRightRemnant = totalRight;
            result.Utilization = (result.BoardCount * input.BoardWidth - result.MinRightRemnant) / (result.BoardCount * input.BoardWidth);
        }

        private static V3Score Score(ShapeNestingResult result)
        {
            return new V3Score
            {
                BoardCount = result.BoardCount,
                UsedLength = result.Utilization,
                PrefixRight = PrefixRightRemnant(result),
                LastRight = result.MinRightRemnant,
                EnvelopeArea = EnvelopeArea(result)
            };
        }

        private static int CompareScore(V3Score a, V3Score b)
        {
            if (ReferenceEquals(a, b))
            {
                return 0;
            }
            if (a == null)
            {
                return 1;
            }
            if (b == null)
            {
                return -1;
            }
            if (a.BoardCount != b.BoardCount)
            {
                return a.BoardCount.CompareTo(b.BoardCount);
            }
            int cmp = CompareAscending(a.UsedLength, b.UsedLength, 1e-8);
            if (cmp != 0)
            {
                return cmp;
            }
            cmp = CompareAscending(a.PrefixRight, b.PrefixRight, 1e-6);
            if (cmp != 0)
            {
                return cmp;
            }
            cmp = CompareDescending(a.LastRight, b.LastRight, 1e-6);
            if (cmp != 0)
            {
                return cmp;
            }
            return CompareAscending(a.EnvelopeArea, b.EnvelopeArea, 1e-6);
        }

        private static void Mutate(ShapeNestingInput input, V3Chromosome chromosome, Random random, double strength)
        {
            int swaps = Math.Max(1, (int)Math.Round(chromosome.Genes.Count * strength * 0.06));
            for (int i = 0; i < swaps; i++)
            {
                int index = random.Next(chromosome.Genes.Count);
                int radius = Math.Max(2, (int)Math.Round(chromosome.Genes.Count * strength * 0.18));
                int j = Math.Max(0, Math.Min(chromosome.Genes.Count - 1, index + random.Next(-radius, radius + 1)));
                V3PartGene tmp = chromosome.Genes[index];
                chromosome.Genes[index] = chromosome.Genes[j];
                chromosome.Genes[j] = tmp;
            }
            for (int i = 0; i < chromosome.Genes.Count; i++)
            {
                if (random.NextDouble() < strength * 0.15)
                {
                    V3PartGene gene = chromosome.Genes[i];
                    gene.Rotation = Rotations[random.Next(Rotations.Length)];
                    chromosome.Genes[i] = gene;
                }
            }
            if (random.NextDouble() < 0.65)
            {
                StableLargeFirstRepair(input, chromosome.Genes);
            }
        }

        private static void StableLargeFirstRepair(ShapeNestingInput input, List<V3PartGene> genes)
        {
            genes.Sort(delegate (V3PartGene a, V3PartGene b)
            {
                double ar = Rank(input, a.TypeIndex);
                double br = Rank(input, b.TypeIndex);
                int cmp = CompareDescending(ar, br, 1e-6);
                if (cmp != 0)
                {
                    return cmp;
                }
                return a.Number.CompareTo(b.Number);
            });
        }

        private static void SortGenesLargeFirst(ShapeNestingInput input, List<V3PartGene> genes)
        {
            StableLargeFirstRepair(input, genes);
        }

        private static double Rank(ShapeNestingInput input, int typeIndex)
        {
            ShapePartType type = input.PartTypes[typeIndex];
            ShapeBounds b = type.Polygon.Bounds();
            return Math.Max(b.Width, b.Height) * 1000000.0 + type.Polygon.Area();
        }

        private static int BestDefaultRotation(ShapePolygon polygon)
        {
            int bestRotation = 0;
            double bestHeight = double.PositiveInfinity;
            double bestWidth = double.PositiveInfinity;
            foreach (int rotation in Rotations)
            {
                ShapeBounds b = polygon.RotateNormalize(rotation).Bounds();
                if (b.Height < bestHeight - 1e-6 || (Math.Abs(b.Height - bestHeight) <= 1e-6 && b.Width < bestWidth))
                {
                    bestHeight = b.Height;
                    bestWidth = b.Width;
                    bestRotation = rotation;
                }
            }
            return bestRotation;
        }

        private static List<int> RotationChoices(int preferred, bool quick)
        {
            List<int> rotations = new List<int>();
            AddRotation(rotations, preferred);
            int[] source = quick ? SeedRotations : Rotations;
            foreach (int rotation in source)
            {
                AddRotation(rotations, rotation);
            }
            return rotations;
        }

        private static void AddRotation(List<int> rotations, int rotation)
        {
            int normalized = ((rotation % 360) + 360) % 360;
            if (!rotations.Contains(normalized))
            {
                rotations.Add(normalized);
            }
        }

        private static bool IsValid(ShapeNestingResult result)
        {
            string ignored;
            return IsValid(result, out ignored);
        }

        private static bool IsValid(ShapeNestingResult result, out string message)
        {
            try
            {
                ShapeNestingValidator.Validate(result);
                message = string.Empty;
                return true;
            }
            catch (System.Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }

        private static void NotifyBest(Action<ShapeNestingResult> bestUpdated, ShapeNestingResult result, Action<string> progress)
        {
            if (bestUpdated == null || result == null)
            {
                return;
            }
            try
            {
                bestUpdated(result);
                if (progress != null)
                {
                    progress("\nV3 已重画最优结果：" + Format(result));
                }
            }
            catch (System.Exception ex)
            {
                if (progress != null)
                {
                    progress("\nV3 最优结果未能重画：" + ex.Message);
                }
            }
            PumpUi(true);
        }

        private static string FormatPartOrder(ShapeNestingInput input)
        {
            List<int> order = new List<int>();
            for (int i = 0; i < input.PartTypes.Count; i++)
            {
                order.Add(i);
            }
            order.Sort(delegate (int a, int b)
            {
                return CompareDescending(Rank(input, a), Rank(input, b), 1e-6);
            });
            List<string> names = new List<string>();
            foreach (int index in order)
            {
                ShapePartType type = input.PartTypes[index];
                ShapeBounds b = type.Polygon.Bounds();
                names.Add(type.Name + "(" + Math.Max(b.Width, b.Height).ToString("0.#", CultureInfo.InvariantCulture) + "," + type.Polygon.Area().ToString("0.#", CultureInfo.InvariantCulture) + ")");
            }
            return string.Join(" > ", names.ToArray());
        }

        private static List<ShapePoint> SelectCandidates(List<ShapePoint> points, int limit)
        {
            points.Sort(delegate (ShapePoint a, ShapePoint b)
            {
                int cmp = a.X.CompareTo(b.X);
                return cmp != 0 ? cmp : a.Y.CompareTo(b.Y);
            });
            if (points.Count <= limit)
            {
                return points;
            }
            List<ShapePoint> selected = new List<ShapePoint>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            int buckets = Math.Min(40, points.Count);
            for (int bucket = 0; bucket < buckets && selected.Count < limit; bucket++)
            {
                int start = (int)Math.Floor(points.Count * bucket / (double)buckets);
                int end = (int)Math.Floor(points.Count * (bucket + 1) / (double)buckets);
                int take = Math.Max(1, limit / buckets);
                for (int i = start; i < end && i < start + take && selected.Count < limit; i++)
                {
                    AddSelected(selected, seen, points[i]);
                }
            }
            for (int i = 0; i < points.Count && selected.Count < limit; i++)
            {
                AddSelected(selected, seen, points[i]);
            }
            selected.Sort(delegate (ShapePoint a, ShapePoint b)
            {
                int cmp = a.X.CompareTo(b.X);
                return cmp != 0 ? cmp : a.Y.CompareTo(b.Y);
            });
            return selected;
        }

        private static void AddCandidate(List<ShapePoint> points, Dictionary<string, bool> seen, double x, double y)
        {
            if (x < -1e-6 || y < -1e-6)
            {
                return;
            }
            string key = PointKey(x, y);
            if (seen.ContainsKey(key))
            {
                return;
            }
            seen.Add(key, true);
            points.Add(new ShapePoint(x, y));
        }

        private static void AddSelected(List<ShapePoint> selected, Dictionary<string, bool> seen, ShapePoint point)
        {
            string key = PointKey(point.X, point.Y);
            if (seen.ContainsKey(key))
            {
                return;
            }
            seen.Add(key, true);
            selected.Add(point);
        }

        private static string PointKey(double x, double y)
        {
            return ((int)Math.Round(x * 1000.0)).ToString(CultureInfo.InvariantCulture) + "," + ((int)Math.Round(y * 1000.0)).ToString(CultureInfo.InvariantCulture);
        }

        private static List<ShapePoint> AnchorPoints(ShapePolygon polygon)
        {
            List<ShapePoint> anchors = new List<ShapePoint>();
            ShapeBounds b = polygon.Bounds();
            anchors.Add(new ShapePoint(b.MinX, b.MinY));
            anchors.Add(new ShapePoint(b.MaxX, b.MinY));
            anchors.Add(new ShapePoint(b.MinX, b.MaxY));
            anchors.Add(new ShapePoint(b.MaxX, b.MaxY));
            anchors.Add(new ShapePoint((b.MinX + b.MaxX) / 2.0, b.MinY));
            anchors.Add(new ShapePoint((b.MinX + b.MaxX) / 2.0, b.MaxY));
            anchors.Add(new ShapePoint(b.MinX, (b.MinY + b.MaxY) / 2.0));
            anchors.Add(new ShapePoint(b.MaxX, (b.MinY + b.MaxY) / 2.0));
            int step = Math.Max(1, polygon.Points.Count / 12);
            for (int i = 0; i < polygon.Points.Count; i += step)
            {
                anchors.Add(polygon.Points[i]);
            }
            return anchors;
        }

        private static double UsedX(List<ShapePlacement> board)
        {
            double used = 0.0;
            foreach (ShapePlacement placement in board)
            {
                used = Math.Max(used, placement.Bounds.MaxX);
            }
            return used;
        }

        private static double TotalPartArea(ShapeNestingInput input)
        {
            double total = 0.0;
            foreach (ShapePartType type in input.PartTypes)
            {
                total += type.Polygon.Area() * type.Quantity;
            }
            return total;
        }

        private static double PrefixRightRemnant(ShapeNestingResult result)
        {
            if (result.BoardCount <= 1 || result.Input == null)
            {
                return 0.0;
            }
            double[] maxX = new double[result.BoardCount];
            foreach (ShapePlacement p in result.Placements)
            {
                if (p.BoardIndex >= 0 && p.BoardIndex < maxX.Length)
                {
                    maxX[p.BoardIndex] = Math.Max(maxX[p.BoardIndex], p.Bounds.MaxX);
                }
            }
            double sum = 0.0;
            for (int i = 0; i < result.BoardCount - 1; i++)
            {
                sum += Math.Max(0.0, result.Input.BoardWidth - maxX[i]);
            }
            return sum;
        }

        private static double EnvelopeArea(ShapeNestingResult result)
        {
            double[] maxX = new double[Math.Max(1, result.BoardCount)];
            double[] maxY = new double[Math.Max(1, result.BoardCount)];
            foreach (ShapePlacement p in result.Placements)
            {
                if (p.BoardIndex >= 0 && p.BoardIndex < maxX.Length)
                {
                    maxX[p.BoardIndex] = Math.Max(maxX[p.BoardIndex], p.Bounds.MaxX);
                    maxY[p.BoardIndex] = Math.Max(maxY[p.BoardIndex], p.Bounds.MaxY);
                }
            }
            double sum = 0.0;
            for (int i = 0; i < maxX.Length; i++)
            {
                sum += maxX[i] * maxY[i];
            }
            return sum;
        }

        private static void CompactBoards(List<List<ShapePlacement>> boards)
        {
            int write = 0;
            for (int read = 0; read < boards.Count; read++)
            {
                if (boards[read].Count == 0)
                {
                    continue;
                }
                if (write != read)
                {
                    boards[write] = boards[read];
                }
                foreach (ShapePlacement placement in boards[write])
                {
                    placement.BoardIndex = write;
                }
                write++;
            }
            while (boards.Count > write)
            {
                boards.RemoveAt(boards.Count - 1);
            }
        }

        private static void RemoveEmptyTrailingBoards(List<List<ShapePlacement>> boards)
        {
            while (boards.Count > 0 && boards[boards.Count - 1].Count == 0)
            {
                boards.RemoveAt(boards.Count - 1);
            }
        }

        private static ShapeBounds TranslateBounds(ShapeBounds bounds, double x, double y)
        {
            return new ShapeBounds { MinX = bounds.MinX + x, MinY = bounds.MinY + y, MaxX = bounds.MaxX + x, MaxY = bounds.MaxY + y };
        }

        private static bool FitsBoard(ShapeNestingInput input, ShapeBounds bounds)
        {
            return bounds.MinX >= -1e-6 && bounds.MinY >= -1e-6 && bounds.MaxX <= input.BoardWidth + 1e-6 && bounds.MaxY <= input.BoardHeight + 1e-6;
        }

        private static double BoundsContact(ShapeBounds a, ShapeBounds b, double tolerance)
        {
            double vertical = 0.0;
            if (Math.Abs(a.MaxX - b.MinX) <= tolerance || Math.Abs(b.MaxX - a.MinX) <= tolerance)
            {
                vertical = Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
            }
            double horizontal = 0.0;
            if (Math.Abs(a.MaxY - b.MinY) <= tolerance || Math.Abs(b.MaxY - a.MinY) <= tolerance)
            {
                horizontal = Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX);
            }
            return Math.Max(0.0, vertical) + Math.Max(0.0, horizontal);
        }

        private static double ShapeNearReward(ShapePolygon a, ShapePolygon b, double maxDistance)
        {
            double best = double.PositiveInfinity;
            int stepA = Math.Max(1, a.Points.Count / 12);
            int stepB = Math.Max(1, b.Points.Count / 12);
            for (int i = 0; i < a.Points.Count; i += stepA)
            {
                ShapePoint pa = a.Points[i];
                for (int j = 0; j < b.Points.Count; j += stepB)
                {
                    ShapePoint pb = b.Points[j];
                    double dx = pa.X - pb.X;
                    double dy = pa.Y - pb.Y;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < best)
                    {
                        best = d;
                    }
                }
            }
            return best > maxDistance ? 0.0 : maxDistance - best;
        }

        private static List<V3PartGene> CloneGenes(List<V3PartGene> genes)
        {
            List<V3PartGene> clone = new List<V3PartGene>();
            foreach (V3PartGene gene in genes)
            {
                clone.Add(gene);
            }
            return clone;
        }

        private static int CompareAscending(double a, double b, double tolerance)
        {
            if (Math.Abs(a - b) <= tolerance)
            {
                return 0;
            }
            return a < b ? -1 : 1;
        }

        private static int CompareDescending(double a, double b, double tolerance)
        {
            if (Math.Abs(a - b) <= tolerance)
            {
                return 0;
            }
            return a > b ? -1 : 1;
        }

        private static void PumpUi(bool force)
        {
            if ((GetAsyncKeyState(0x1B) & 0x8000) != 0)
            {
                throw new OperationCanceledException("V3 排料已按 Esc 取消。");
            }
            DateTime now = DateTime.UtcNow;
            if (!force && now < _nextUiPump)
            {
                return;
            }
            _nextUiPump = now.AddMilliseconds(250);
            try
            {
                System.Windows.Forms.Application.DoEvents();
            }
            catch
            {
            }
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);
    }

    internal struct V3PartGene
    {
        public int TypeIndex;
        public int Number;
        public int Rotation;
    }

    internal sealed class V3Chromosome
    {
        public List<V3PartGene> Genes = new List<V3PartGene>();
        public ShapeNestingResult Result;
        public V3Score Score;

        public V3Chromosome CloneWithResult()
        {
            V3Chromosome clone = CloneGenesOnly();
            clone.Result = Result;
            clone.Score = Score;
            return clone;
        }

        public V3Chromosome CloneGenesOnly()
        {
            V3Chromosome clone = new V3Chromosome();
            foreach (V3PartGene gene in Genes)
            {
                clone.Genes.Add(gene);
            }
            return clone;
        }
    }

    internal sealed class V3Score
    {
        public int BoardCount;
        public double UsedLength;
        public double PrefixRight;
        public double LastRight;
        public double EnvelopeArea;
    }
}
