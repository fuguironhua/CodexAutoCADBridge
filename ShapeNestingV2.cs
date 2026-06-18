using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace CodexAutoCADBridge
{
    internal static class ShapeNestingGroupWorkflow
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

                PromptPointOptions pointOptions = new PromptPointOptions("\nSpecify drawing origin for V2 group nesting result");
                PromptPointResult pointResult = editor.GetPoint(pointOptions);
                if (pointResult.Status != PromptStatus.OK)
                {
                    return;
                }

                editor.WriteMessage("\nCalculating V2 group nesting...");
                bool drewBest = false;
                ShapeNestingResult result = ShapeNestingGroupSolver.Solve(input, delegate (string message)
                {
                    editor.WriteMessage(message);
                }, delegate (ShapeNestingResult bestResult)
                {
                    ShapeNestingValidator.Validate(bestResult);
                    ShapeNestingDrawer.Redraw(doc, bestResult, pointResult.Value);
                    drewBest = true;
                });
                ShapeNestingValidator.Validate(result);
                editor.WriteMessage("\nV2 group nesting result: " + result.BoardCount.ToString(CultureInfo.InvariantCulture) +
                    " sheets, used board length " + (result.Utilization * 100.0).ToString("0.00", CultureInfo.InvariantCulture) +
                    "%, last sheet right remnant " + result.MinRightRemnant.ToString("0.###", CultureInfo.InvariantCulture) +
                    ", total right remnant " + result.TotalRightRemnant.ToString("0.###", CultureInfo.InvariantCulture) + ".");
                if (!drewBest)
                {
                    ShapeNestingDrawer.Redraw(doc, result, pointResult.Value);
                }
            }
            catch (System.Exception ex)
            {
                if (ex is OperationCanceledException)
                {
                    editor.WriteMessage("\nCDX_NEST_GROUP_V2 canceled.");
                    return;
                }
                editor.WriteMessage("\nCDX_NEST_GROUP_V2 failed: " + ex.Message);
            }
        }
    }

    internal static class ShapeNestingGroupSolver
    {
        private const int PopulationSize = 14;
        private const int EliteCount = 4;
        private const int RandomImmigrantCount = 2;
        private const int Generations = 42;
        private const int MaxCandidates = 900;
        private const int MaxBackfillCandidates = 2600;
        private const int MaxExchangeCandidates = 8;
        private const int MaxExchangeEvictions = 2;
        private const int MaxTemplatesPerCount = 20;
        private const int MaxTemplateGroupCount = 16;
        private const int MaxInternalGroupCandidates = 420;
        private const int TemplateBuildBudgetMs = 25000;
        private const double Gap = 2.0;
        private static readonly int[] SingleRotations = new int[] { 0, 45, 90, 135, 180, 225, 270, 315 };
        private static DateTime _nextUiPump;
        private static DateTime _templateBuildDeadline;

        public static ShapeNestingResult Solve(ShapeNestingInput input, Action<string> progress, Action<ShapeNestingResult> bestUpdated)
        {
            DateTime started = DateTime.UtcNow;
            _nextUiPump = started;
            _templateBuildDeadline = started.AddMilliseconds(TemplateBuildBudgetMs);

            List<List<GroupTemplate>> library = BuildTemplateLibrary(input, progress);
            int seed = unchecked(Environment.TickCount ^ (int)DateTime.UtcNow.Ticks);
            Random random = new Random(seed);
            if (progress != null)
            {
                progress("\n" + CodexBridgeVersion.Version + " group GA enabled: true large-part-first order, partial split backfill, dense old-board candidates, no GA time limit.");
                progress("\nV2 part order: " + FormatPartOrder(input) + ".");
                progress("\nV2 random seed: " + seed.ToString(CultureInfo.InvariantCulture) + ".");
                PumpUi(true);
            }

            List<GroupChromosome> population = CreateInitialPopulation(input, library, random);
            ShapeNestingResult best = null;
            GroupScore bestScore = null;

            for (int generation = 0; generation <= Generations; generation++)
            {
                DateTime generationStart = DateTime.UtcNow;
                int valid = 0;
                for (int i = 0; i < population.Count; i++)
                {
                    PumpUi(false);
                    DateTime individualStart = DateTime.UtcNow;
                    GroupChromosome c = population[i];
                    if (c.Result == null)
                    {
                        c.Result = PlaceChromosome(input, library, c);
                        if (c.Result != null && IsValid(c.Result))
                        {
                            c.Score = ScoreResult(c.Result);
                            c.Result.Generation = generation;
                            c.Result.Individual = i + 1;
                        }
                        else
                        {
                            c.Result = null;
                            c.Score = null;
                        }
                    }
                    if (c.Result != null)
                    {
                        valid++;
                        if (best == null || CompareScores(c.Score, bestScore) < 0)
                        {
                            best = c.Result;
                            bestScore = c.Score;
                            NotifyBest(bestUpdated, best, progress);
                        }
                    }
                    if (progress != null)
                    {
                        string individualText = c.Result != null ? Format(c.Result) : "skipped";
                        progress("\nV2 gen " + generation.ToString(CultureInfo.InvariantCulture) +
                            " individual " + (i + 1).ToString(CultureInfo.InvariantCulture) + "/" + population.Count.ToString(CultureInfo.InvariantCulture) +
                            "  " + individualText +
                            "  elapsed: " + (DateTime.UtcNow - started).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s" +
                            "  eval: " + (DateTime.UtcNow - individualStart).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s");
                        PumpUi(false);
                    }
                }

                population.Sort(delegate (GroupChromosome a, GroupChromosome b)
                {
                    return CompareScores(a.Score, b.Score);
                });

                if (progress != null)
                {
                    string current = population.Count > 0 && population[0].Result != null ? Format(population[0].Result) : "no valid result";
                    string bestText = best != null ? Format(best) : "none";
                    progress("\nV2 gen " + generation.ToString(CultureInfo.InvariantCulture) +
                        " valid " + valid.ToString(CultureInfo.InvariantCulture) + "/" + population.Count.ToString(CultureInfo.InvariantCulture) +
                        " current: " + current +
                        " best: " + bestText +
                        " elapsed: " + (DateTime.UtcNow - started).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s" +
                        " gen time: " + (DateTime.UtcNow - generationStart).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s");
                    PumpUi(true);
                }

                if (generation == Generations)
                {
                    break;
                }
                population = NextGeneration(population, input, library, random);
            }

            if (best == null)
            {
                throw new InvalidOperationException("V2 did not find a complete group nesting result.");
            }
            if (progress != null)
            {
                progress("\nV2 best selected: " + Format(best));
                PumpUi(true);
            }
            return best;
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
                    progress("\nV2 redrew best: " + Format(result));
                }
            }
            catch (System.Exception ex)
            {
                if (progress != null)
                {
                    progress("\nV2 best was not drawn: " + ex.Message);
                }
            }
            PumpUi(true);
        }

        private static bool IsValid(ShapeNestingResult result)
        {
            if (result == null)
            {
                return false;
            }
            try
            {
                ShapeNestingValidator.Validate(result);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static List<GroupChromosome> CreateInitialPopulation(ShapeNestingInput input, List<List<GroupTemplate>> library, Random random)
        {
            List<GroupChromosome> population = new List<GroupChromosome>();
            population.Add(BuildChromosome(input, library, random, 0.95, false));
            population.Add(BuildChromosome(input, library, random, 0.75, false));
            population.Add(BuildChromosome(input, library, random, 0.55, true));
            while (population.Count < PopulationSize)
            {
                population.Add(BuildChromosome(input, library, random, 0.35 + random.NextDouble() * 0.6, random.NextDouble() < 0.65));
            }
            return population;
        }

        private static GroupChromosome BuildChromosome(ShapeNestingInput input, List<List<GroupTemplate>> library, Random random, double largeGroupBias, bool freeOrder)
        {
            GroupChromosome c = new GroupChromosome();
            c.FreeOrder = freeOrder;
            for (int typeIndex = 0; typeIndex < input.PartTypes.Count; typeIndex++)
            {
                int remaining = input.PartTypes[typeIndex].Quantity;
                while (remaining > 0)
                {
                    int count = ChooseAvailableGroupCount(library[typeIndex], remaining, random, largeGroupBias);
                    int variantCount = CountTemplates(library[typeIndex], count);
                    c.Genes.Add(new GroupGene
                    {
                        TypeIndex = typeIndex,
                        Count = count,
                        VariantIndex = variantCount == 0 ? 0 : random.Next(variantCount)
                    });
                    remaining -= count;
                }
            }
            SortGenesByPartSizeThenGroup(input, c.Genes);
            if (c.FreeOrder)
            {
                ShuffleBands(c.Genes, random, 0.35);
                MutateGeneOrder(c.Genes, random);
                MutateGeneOrder(c.Genes, random);
            }
            else
            {
                ShuffleBands(c.Genes, random, 0.06);
            }
            return c;
        }

        private static int ChooseGroupCount(int remaining, Random random, double largeGroupBias)
        {
            if (remaining <= 1)
            {
                return 1;
            }

            double directMaxChance = 0.08 + largeGroupBias * 0.22;
            if (random.NextDouble() < directMaxChance)
            {
                return remaining;
            }

            double exponent = 1.25 - Math.Min(0.95, largeGroupBias);
            double shaped = Math.Pow(random.NextDouble(), Math.Max(0.18, exponent));
            int count = 1 + (int)Math.Floor(shaped * remaining);
            if (random.NextDouble() < 0.28)
            {
                int near = Math.Max(1, (int)Math.Round(remaining * (0.25 + random.NextDouble() * 0.55)));
                count = Math.Max(count, near);
            }
            return Math.Max(1, Math.Min(remaining, count));
        }

        private static int ChooseAvailableGroupCount(List<GroupTemplate> templates, int remaining, Random random, double largeGroupBias)
        {
            int maxAvailable = 1;
            foreach (GroupTemplate template in templates)
            {
                if (template.Count <= remaining && template.Count > maxAvailable)
                {
                    maxAvailable = template.Count;
                }
            }
            int count = Math.Min(maxAvailable, ChooseGroupCount(Math.Min(remaining, maxAvailable), random, largeGroupBias));
            for (int n = count; n >= 1; n--)
            {
                if (CountTemplates(templates, n) > 0)
                {
                    return n;
                }
            }
            for (int n = count + 1; n <= remaining; n++)
            {
                if (CountTemplates(templates, n) > 0)
                {
                    return n;
                }
            }
            return 1;
        }

        private static bool TemplateBuildExpired()
        {
            return DateTime.UtcNow > _templateBuildDeadline;
        }

        private static List<GroupChromosome> NextGeneration(List<GroupChromosome> population, ShapeNestingInput input, List<List<GroupTemplate>> library, Random random)
        {
            List<GroupChromosome> next = new List<GroupChromosome>();
            int elites = Math.Min(EliteCount, population.Count);
            for (int i = 0; i < elites; i++)
            {
                next.Add(population[i].Clone());
            }
            int immigrantStart = Math.Max(elites, PopulationSize - RandomImmigrantCount);
            while (next.Count < PopulationSize)
            {
                if (next.Count >= immigrantStart)
                {
                    next.Add(BuildChromosome(input, library, random, 0.20 + random.NextDouble() * 0.75, true));
                }
                else
                {
                    GroupChromosome parent = population[random.Next(Math.Max(1, Math.Min(population.Count, elites + 5)))];
                    GroupChromosome child = parent.CloneWithoutResult();
                    Mutate(child, input, library, random);
                    next.Add(child);
                }
            }
            return next;
        }

        private static void Mutate(GroupChromosome c, ShapeNestingInput input, List<List<GroupTemplate>> library, Random random)
        {
            if (random.NextDouble() < 0.24)
            {
                c.FreeOrder = true;
            }
            if (random.NextDouble() < 0.45)
            {
                ShuffleBands(c.Genes, random, 0.12);
            }
            if (random.NextDouble() < 0.35)
            {
                MutateGroupSize(c, input, library, random);
            }
            if (random.NextDouble() < 0.65)
            {
                int index = random.Next(c.Genes.Count);
                GroupGene g = c.Genes[index];
                int variantCount = CountTemplates(library[g.TypeIndex], g.Count);
                if (variantCount > 1)
                {
                    g.VariantIndex = random.Next(variantCount);
                    c.Genes[index] = g;
                }
            }
            if (c.FreeOrder)
            {
                MutateGeneOrder(c.Genes, random);
                if (random.NextDouble() < 0.35)
                {
                    MutateGeneOrder(c.Genes, random);
                }
            }
            else
            {
                SortGenesByPartSizeThenGroup(input, c.Genes);
            }
        }

        private static void MutateGroupSize(GroupChromosome c, ShapeNestingInput input, List<List<GroupTemplate>> library, Random random)
        {
            int typeIndex = random.Next(input.PartTypes.Count);
            List<GroupGene> keep = new List<GroupGene>();
            int total = 0;
            foreach (GroupGene g in c.Genes)
            {
                if (g.TypeIndex == typeIndex)
                {
                    total += g.Count;
                }
                else
                {
                    keep.Add(g);
                }
            }
            double bias = 0.25 + random.NextDouble() * 0.7;
            while (total > 0)
            {
                int count = ChooseAvailableGroupCount(library[typeIndex], total, random, bias);
                int variantCount = CountTemplates(library[typeIndex], count);
                keep.Add(new GroupGene { TypeIndex = typeIndex, Count = count, VariantIndex = variantCount == 0 ? 0 : random.Next(variantCount) });
                total -= count;
            }
            c.Genes = keep;
            SortGenesByPartSizeThenGroup(input, c.Genes);
            ShuffleBands(c.Genes, random, 0.10);
        }

        private static ShapeNestingResult PlaceChromosome(ShapeNestingInput input, List<List<GroupTemplate>> library, GroupChromosome c)
        {
            ShapeNestingResult result = new ShapeNestingResult();
            result.Input = input;
            result.TotalArea = TotalPartArea(input);
            List<List<ShapePlacement>> boards = new List<List<ShapePlacement>>();
            int[] nextNumber = new int[input.PartTypes.Count];
            for (int i = 0; i < nextNumber.Length; i++)
            {
                nextNumber[i] = 1;
            }

            foreach (GroupGene gene in c.Genes)
            {
                PumpUi(false);
                PlaceGeneRecursive(input, library, result, boards, gene.TypeIndex, gene.Count, gene.VariantIndex, nextNumber, true);
            }

            BackfillLaterBoards(input, result, boards);
            ExchangeBackfillEarlierBoards(input, result, boards);
            CompactBoards(boards);
            FinalizeResult(input, result, boards);
            return result;
        }

        private static bool PlaceGeneRecursive(ShapeNestingInput input, List<List<GroupTemplate>> library, ShapeNestingResult result, List<List<ShapePlacement>> boards, int typeIndex, int count, int variantIndex, int[] nextNumber, bool allowSplit)
        {
            PumpUi(false);
            GroupTemplate template = SelectTemplate(library[typeIndex], count, variantIndex);
            ShapePartInstance groupPart = CreateGroupPart(input, typeIndex, template, nextNumber);
            ShapePlacement placement = BestPlacement(input, boards, groupPart, template);
            if (placement != null)
            {
                boards[placement.BoardIndex].Add(placement);
                result.Placements.Add(placement);
                return true;
            }

            nextNumber[typeIndex] -= count;
            if (boards.Count == 0)
            {
                return PlaceGeneOnNewBoard(input, result, boards, typeIndex, count, template, nextNumber);
            }
            if (allowSplit && count > 1)
            {
                int left = count / 2;
                int right = count - left;
                PlacementSnapshot snapshot = TakeSnapshot(result, boards, nextNumber);
                bool placedLeft = PlaceGeneExistingBoardsOnly(input, library, result, boards, typeIndex, left, variantIndex, nextNumber, true);
                bool placedRight = placedLeft && PlaceGeneExistingBoardsOnly(input, library, result, boards, typeIndex, right, variantIndex + 1, nextNumber, true);
                if (placedLeft && placedRight)
                {
                    return true;
                }
                RestoreSnapshot(snapshot, result, boards, nextNumber);
            }

            if (allowSplit && count > 1 && boards.Count > 0)
            {
                PlacementSnapshot snapshot = TakeSnapshot(result, boards, nextNumber);
                if (PlaceAsSmallerGroupsOnExistingBoards(input, library, result, boards, typeIndex, count, variantIndex, nextNumber))
                {
                    return true;
                }
                RestoreSnapshot(snapshot, result, boards, nextNumber);
            }

            return PlaceGeneOnNewBoard(input, result, boards, typeIndex, count, template, nextNumber);
        }

        private static bool PlaceGeneExistingBoardsOnly(ShapeNestingInput input, List<List<GroupTemplate>> library, ShapeNestingResult result, List<List<ShapePlacement>> boards, int typeIndex, int count, int variantIndex, int[] nextNumber, bool allowSplit)
        {
            PumpUi(false);
            if (boards.Count == 0)
            {
                return false;
            }
            GroupTemplate template = SelectTemplate(library[typeIndex], count, variantIndex);
            ShapePartInstance groupPart = CreateGroupPart(input, typeIndex, template, nextNumber);
            ShapePlacement placement = BestPlacement(input, boards, groupPart, template);
            if (placement != null)
            {
                boards[placement.BoardIndex].Add(placement);
                result.Placements.Add(placement);
                return true;
            }
            nextNumber[typeIndex] -= count;

            if (allowSplit && count > 1)
            {
                int left = count / 2;
                int right = count - left;
                PlacementSnapshot snapshot = TakeSnapshot(result, boards, nextNumber);
                bool placedLeft = PlaceGeneExistingBoardsOnly(input, library, result, boards, typeIndex, left, variantIndex, nextNumber, true);
                bool placedRight = placedLeft && PlaceGeneExistingBoardsOnly(input, library, result, boards, typeIndex, right, variantIndex + 1, nextNumber, true);
                if (placedLeft && placedRight)
                {
                    return true;
                }
                RestoreSnapshot(snapshot, result, boards, nextNumber);

                snapshot = TakeSnapshot(result, boards, nextNumber);
                if (PlaceAsSmallerGroupsOnExistingBoards(input, library, result, boards, typeIndex, count, variantIndex, nextNumber))
                {
                    return true;
                }
                RestoreSnapshot(snapshot, result, boards, nextNumber);
            }
            return false;
        }

        private static bool PlaceGeneOnNewBoard(ShapeNestingInput input, ShapeNestingResult result, List<List<ShapePlacement>> boards, int typeIndex, int count, GroupTemplate template, int[] nextNumber)
        {
            ShapePartInstance groupPart = CreateGroupPart(input, typeIndex, template, nextNumber);
            boards.Add(new List<ShapePlacement>());
            ShapePlacement placement = MakePlacement(groupPart, template, boards.Count - 1, 0.0, 0.0);
            if (!FitsBoard(input, placement.Bounds))
            {
                boards.RemoveAt(boards.Count - 1);
                nextNumber[typeIndex] -= count;
                return false;
            }
            boards[placement.BoardIndex].Add(placement);
            result.Placements.Add(placement);
            return true;
        }

        private static bool PlaceAsSmallerGroupsOnExistingBoards(ShapeNestingInput input, List<List<GroupTemplate>> library, ShapeNestingResult result, List<List<ShapePlacement>> boards, int typeIndex, int count, int variantIndex, int[] nextNumber)
        {
            int remaining = count;
            while (remaining > 0)
            {
                PumpUi(false);
                bool placed = false;
                int tryCount = Math.Min(remaining, Math.Max(1, count / 2));
                while (tryCount >= 1)
                {
                    int available = NearestAvailableTemplateCount(library[typeIndex], tryCount);
                    if (available <= 0)
                    {
                        available = 1;
                    }
                    if (available > remaining)
                    {
                        available = remaining;
                    }
                    GroupTemplate template = SelectTemplate(library[typeIndex], available, variantIndex + remaining);
                    ShapePartInstance groupPart = CreateGroupPart(input, typeIndex, template, nextNumber);
                    ShapePlacement placement = BestPlacement(input, boards, groupPart, template);
                    if (placement != null)
                    {
                        boards[placement.BoardIndex].Add(placement);
                        result.Placements.Add(placement);
                        remaining -= available;
                        placed = true;
                        break;
                    }
                    nextNumber[typeIndex] -= available;
                    tryCount = available - 1;
                }
                if (!placed)
                {
                    return false;
                }
            }
            return true;
        }

        private static int NearestAvailableTemplateCount(List<GroupTemplate> templates, int desired)
        {
            for (int n = desired; n >= 1; n--)
            {
                if (CountTemplates(templates, n) > 0)
                {
                    return n;
                }
            }
            return 0;
        }

        private static PlacementSnapshot TakeSnapshot(ShapeNestingResult result, List<List<ShapePlacement>> boards, int[] nextNumber)
        {
            PlacementSnapshot snapshot = new PlacementSnapshot();
            snapshot.PlacementCount = result.Placements.Count;
            snapshot.BoardCount = boards.Count;
            snapshot.BoardPlacementCounts = new int[boards.Count];
            for (int i = 0; i < boards.Count; i++)
            {
                snapshot.BoardPlacementCounts[i] = boards[i].Count;
            }
            snapshot.NextNumber = (int[])nextNumber.Clone();
            return snapshot;
        }

        private static void RestoreSnapshot(PlacementSnapshot snapshot, ShapeNestingResult result, List<List<ShapePlacement>> boards, int[] nextNumber)
        {
            if (result.Placements.Count > snapshot.PlacementCount)
            {
                result.Placements.RemoveRange(snapshot.PlacementCount, result.Placements.Count - snapshot.PlacementCount);
            }
            while (boards.Count > snapshot.BoardCount)
            {
                boards.RemoveAt(boards.Count - 1);
            }
            for (int i = 0; i < boards.Count; i++)
            {
                int count = snapshot.BoardPlacementCounts[i];
                if (boards[i].Count > count)
                {
                    boards[i].RemoveRange(count, boards[i].Count - count);
                }
            }
            for (int i = 0; i < nextNumber.Length; i++)
            {
                nextNumber[i] = snapshot.NextNumber[i];
            }
        }

        private static ShapePlacement BestPlacement(ShapeNestingInput input, List<List<ShapePlacement>> boards, ShapePartInstance groupPart, GroupTemplate template)
        {
            List<int> boardOrder = BoardFillOrder(boards);
            foreach (int boardIndex in boardOrder)
            {
                ShapePlacement placement = BestPlacementOnSingleBoard(input, boards, groupPart, template, boardIndex);
                if (placement != null)
                {
                    return placement;
                }
            }
            return null;
        }

        private static List<int> BoardFillOrder(List<List<ShapePlacement>> boards)
        {
            List<int> order = new List<int>();
            for (int i = 0; i < boards.Count; i++)
            {
                order.Add(i);
            }
            order.Sort(delegate (int a, int b)
            {
                return a.CompareTo(b);
            });
            return order;
        }

        private static void BackfillLaterBoards(ShapeNestingInput input, ShapeNestingResult result, List<List<ShapePlacement>> boards)
        {
            if (boards.Count < 2)
            {
                return;
            }
            bool movedAny = true;
            int passes = 0;
            while (movedAny && passes < 4)
            {
                movedAny = false;
                passes++;
                for (int sourceBoard = boards.Count - 1; sourceBoard >= 1; sourceBoard--)
                {
                    List<ShapePlacement> moveOrder = SourceBoardMoveOrder(boards[sourceBoard]);
                    foreach (ShapePlacement moving in moveOrder)
                    {
                        PumpUi(false);
                        if (!boards[sourceBoard].Contains(moving))
                        {
                            continue;
                        }
                        GroupTemplate template = TemplateFromPlacement(moving);
                        ShapePartInstance part = moving.Part;
                        RemovePlacement(result, boards[sourceBoard], moving);
                        ShapePlacement best = BestPlacementOnBoards(input, boards, part, template, 0, sourceBoard - 1, true);
                        if (best != null)
                        {
                            boards[best.BoardIndex].Add(best);
                            result.Placements.Add(best);
                            movedAny = true;
                        }
                        else if (TryPartialBackfillSplitPlacement(input, result, boards, moving, sourceBoard))
                        {
                            movedAny = true;
                        }
                        else if (TryBackfillSplitPlacement(input, result, boards, moving, sourceBoard))
                        {
                            movedAny = true;
                        }
                        else
                        {
                            boards[sourceBoard].Add(moving);
                            result.Placements.Add(moving);
                        }
                    }
                }
                RemoveEmptyTrailingBoards(boards);
            }
        }

        private static List<ShapePlacement> SourceBoardMoveOrder(List<ShapePlacement> board)
        {
            List<ShapePlacement> order = new List<ShapePlacement>(board);
            order.Sort(delegate (ShapePlacement a, ShapePlacement b)
            {
                int cmp = b.Bounds.MaxX.CompareTo(a.Bounds.MaxX);
                if (cmp != 0)
                {
                    return cmp;
                }
                cmp = b.Bounds.MaxY.CompareTo(a.Bounds.MaxY);
                if (cmp != 0)
                {
                    return cmp;
                }
                return b.Bounds.Width.CompareTo(a.Bounds.Width);
            });
            return order;
        }

        private static bool TryBackfillSplitPlacement(ShapeNestingInput input, ShapeNestingResult result, List<List<ShapePlacement>> boards, ShapePlacement moving, int sourceBoard)
        {
            if (moving.Part.Components == null || moving.Part.Components.Count <= 1)
            {
                return false;
            }

            List<ShapePartComponent> components = new List<ShapePartComponent>(moving.Part.Components);
            components.Sort(delegate (ShapePartComponent a, ShapePartComponent b)
            {
                return b.LocalPolygon.Area().CompareTo(a.LocalPolygon.Area());
            });

            List<ShapePlacement> inserted = new List<ShapePlacement>();
            foreach (ShapePartComponent component in components)
            {
                PumpUi(false);
                GroupTemplate template = TemplateFromSingleComponent(component.LocalPolygon);
                ShapePartInstance part = PartFromComponent(component, template);
                ShapePlacement placement = BestPlacementOnBoards(input, boards, part, template, 0, sourceBoard - 1, true);
                if (placement == null)
                {
                    placement = BestRotatedComponentPlacementOnBoards(input, boards, component, 0, sourceBoard - 1, true);
                }
                if (placement == null)
                {
                    foreach (ShapePlacement added in inserted)
                    {
                        if (added.BoardIndex >= 0 && added.BoardIndex < boards.Count)
                        {
                            boards[added.BoardIndex].Remove(added);
                        }
                        result.Placements.Remove(added);
                    }
                    return false;
                }
                boards[placement.BoardIndex].Add(placement);
                result.Placements.Add(placement);
                inserted.Add(placement);
            }
            return true;
        }

        private static bool TryPartialBackfillSplitPlacement(ShapeNestingInput input, ShapeNestingResult result, List<List<ShapePlacement>> boards, ShapePlacement moving, int sourceBoard)
        {
            if (moving.Part.Components == null || moving.Part.Components.Count <= 1)
            {
                return false;
            }

            List<ShapePartComponent> components = ComponentsInPackingOrder(moving);
            List<ShapePlacement> moved = new List<ShapePlacement>();
            List<ShapePartComponent> remaining = new List<ShapePartComponent>();
            foreach (ShapePartComponent component in components)
            {
                PumpUi(false);
                GroupTemplate template = TemplateFromSingleComponent(component.LocalPolygon);
                ShapePartInstance part = PartFromComponent(component, template);
                ShapePlacement placement = BestPlacementOnBoards(input, boards, part, template, 0, sourceBoard - 1, true);
                if (placement == null)
                {
                    placement = BestRotatedComponentPlacementOnBoards(input, boards, component, 0, sourceBoard - 1, true);
                }
                if (placement != null)
                {
                    boards[placement.BoardIndex].Add(placement);
                    result.Placements.Add(placement);
                    moved.Add(placement);
                }
                else
                {
                    remaining.Add(component);
                }
            }

            if (moved.Count == 0)
            {
                return false;
            }

            foreach (ShapePartComponent component in remaining)
            {
                ShapePlacement restored = RestoreComponentOnSourceBoard(component, sourceBoard, moving.X, moving.Y);
                boards[sourceBoard].Add(restored);
                result.Placements.Add(restored);
            }
            return true;
        }

        private static void ExchangeBackfillEarlierBoards(ShapeNestingInput input, ShapeNestingResult result, List<List<ShapePlacement>> boards)
        {
            if (boards.Count < 2)
            {
                return;
            }

            bool changed = true;
            int passes = 0;
            while (changed && passes < 3)
            {
                changed = false;
                passes++;
                for (int sourceBoard = boards.Count - 1; sourceBoard >= 1; sourceBoard--)
                {
                    List<ShapePlacement> sourceOrder = SourceBoardMoveOrder(boards[sourceBoard]);
                    int limit = Math.Min(MaxExchangeCandidates, sourceOrder.Count);
                    for (int i = 0; i < limit; i++)
                    {
                        ShapePlacement moving = sourceOrder[i];
                        PumpUi(false);
                        if (!boards[sourceBoard].Contains(moving))
                        {
                            continue;
                        }

                        PlacementSnapshot snapshot = TakeSnapshot(result, boards, BuildNextNumberState(input, result));
                        RemovePlacement(result, boards[sourceBoard], moving);

                        if (TryExchangePlacement(input, result, boards, moving, sourceBoard))
                        {
                            changed = true;
                            break;
                        }

                        RestoreSnapshot(snapshot, result, boards, snapshot.NextNumber);
                    }
                    if (changed)
                    {
                        break;
                    }
                }
            }
        }

        private static int[] BuildNextNumberState(ShapeNestingInput input, ShapeNestingResult result)
        {
            int[] nextNumber = new int[input.PartTypes.Count];
            for (int i = 0; i < nextNumber.Length; i++)
            {
                nextNumber[i] = 1;
            }
            foreach (ShapePlacement placement in result.Placements)
            {
                if (placement.Part == null)
                {
                    continue;
                }
                int typeIndex = placement.Part.TypeIndex;
                if (typeIndex < 0 || typeIndex >= nextNumber.Length)
                {
                    continue;
                }
                nextNumber[typeIndex] = Math.Max(nextNumber[typeIndex], placement.Part.Number + 1);
            }
            return nextNumber;
        }

        private static bool TryExchangePlacement(ShapeNestingInput input, ShapeNestingResult result, List<List<ShapePlacement>> boards, ShapePlacement moving, int sourceBoard)
        {
            if (moving.Part == null)
            {
                return false;
            }

            List<GroupTemplate> rotatedCandidates = BuildTemplateCandidatesForPlacement(moving);
            int targetLimit = Math.Min(sourceBoard - 1, boards.Count - 1);
            if (targetLimit < 0)
            {
                return false;
            }

            for (int boardIndex = 0; boardIndex <= targetLimit; boardIndex++)
            {
                foreach (GroupTemplate template in rotatedCandidates)
                {
                    ShapePartInstance part = PartForTemplate(moving.Part, template);
                    ShapePlacement placement = BestPlacementOnSingleBoard(input, boards, part, template, boardIndex, true);
                    if (placement != null)
                    {
                        boards[placement.BoardIndex].Add(placement);
                        result.Placements.Add(placement);
                        return true;
                    }
                }
            }

            for (int boardIndex = 0; boardIndex <= targetLimit; boardIndex++)
            {
                if (TryEvictTargetBoardAndPlace(input, result, boards, moving, rotatedCandidates, sourceBoard, boardIndex))
                {
                    return true;
                }
            }

            List<ShapePlacement> victimCandidates = new List<ShapePlacement>(boards[sourceBoard]);
            victimCandidates.Sort(delegate (ShapePlacement a, ShapePlacement b)
            {
                int cmp = a.Bounds.MaxX.CompareTo(b.Bounds.MaxX);
                if (cmp != 0)
                {
                    return cmp;
                }
                return a.Bounds.MaxY.CompareTo(b.Bounds.MaxY);
            });
            int evictions = Math.Min(MaxExchangeEvictions, victimCandidates.Count);
            for (int e = 0; e < evictions; e++)
            {
                ShapePlacement victim = victimCandidates[e];
                if (victim == moving)
                {
                    continue;
                }
                if (!boards[sourceBoard].Contains(victim))
                {
                    continue;
                }

                PlacementSnapshot snapshot = TakeSnapshot(result, boards, BuildNextNumberState(input, result));
                RemovePlacement(result, boards[sourceBoard], victim);
                foreach (GroupTemplate template in rotatedCandidates)
                {
                    ShapePartInstance part = PartForTemplate(moving.Part, template);
                    ShapePlacement placement = BestPlacementOnBoards(input, boards, part, template, 0, sourceBoard - 1, true);
                    if (placement != null)
                    {
                        boards[placement.BoardIndex].Add(placement);
                        result.Placements.Add(placement);
                        if (ReinsertPlacementOnLaterBoards(input, result, boards, victim, sourceBoard))
                        {
                            return true;
                        }
                    }
                }
                RestoreSnapshot(snapshot, result, boards, snapshot.NextNumber);
            }

            return false;
        }

        private static bool TryEvictTargetBoardAndPlace(ShapeNestingInput input, ShapeNestingResult result, List<List<ShapePlacement>> boards, ShapePlacement moving, List<GroupTemplate> movingTemplates, int sourceBoard, int targetBoard)
        {
            if (targetBoard < 0 || targetBoard >= boards.Count || sourceBoard < 0 || sourceBoard >= boards.Count)
            {
                return false;
            }
            List<ShapePlacement> victims = SourceBoardMoveOrder(boards[targetBoard]);
            int victimLimit = Math.Min(MaxExchangeEvictions, victims.Count);
            for (int v = 0; v < victimLimit; v++)
            {
                ShapePlacement victim = victims[v];
                if (!boards[targetBoard].Contains(victim))
                {
                    continue;
                }

                PlacementSnapshot snapshot = TakeSnapshot(result, boards, BuildNextNumberState(input, result));
                RemovePlacement(result, boards[targetBoard], victim);
                foreach (GroupTemplate template in movingTemplates)
                {
                    ShapePartInstance movingPart = PartForTemplate(moving.Part, template);
                    ShapePlacement placement = BestPlacementOnSingleBoard(input, boards, movingPart, template, targetBoard, true);
                    if (placement == null)
                    {
                        continue;
                    }
                    boards[targetBoard].Add(placement);
                    result.Placements.Add(placement);
                    if (ReinsertPlacementOnLaterBoards(input, result, boards, victim, Math.Min(sourceBoard, boards.Count - 1)))
                    {
                        return true;
                    }
                }
                RestoreSnapshot(snapshot, result, boards, snapshot.NextNumber);
            }
            return false;
        }

        private static bool ReinsertPlacementOnLaterBoards(ShapeNestingInput input, ShapeNestingResult result, List<List<ShapePlacement>> boards, ShapePlacement placement, int sourceBoard)
        {
            List<GroupTemplate> templates = BuildTemplateCandidatesForPlacement(placement);
            foreach (GroupTemplate template in templates)
            {
                ShapePartInstance part = PartForTemplate(placement.Part, template);
                ShapePlacement restored = BestPlacementOnBoards(input, boards, part, template, sourceBoard, boards.Count - 1, true);
                if (restored != null)
                {
                    boards[restored.BoardIndex].Add(restored);
                    result.Placements.Add(restored);
                    return true;
                }
            }
            boards[sourceBoard].Add(placement);
            result.Placements.Add(placement);
            return false;
        }

        private static ShapePlacement BestRotatedComponentPlacementOnBoards(ShapeNestingInput input, List<List<ShapePlacement>> boards, ShapePartComponent component, int firstBoard, int lastBoard, bool denseBackfill)
        {
            foreach (GroupTemplate template in BuildRotatedSingleTemplateCandidates(component.LocalPolygon, 8))
            {
                ShapePartInstance part = PartFromComponent(component, template);
                ShapePlacement placement = BestPlacementOnBoards(input, boards, part, template, firstBoard, lastBoard, denseBackfill);
                if (placement != null)
                {
                    return placement;
                }
            }
            return null;
        }

        private static List<GroupTemplate> BuildTemplateCandidatesForPlacement(ShapePlacement placement)
        {
            if (placement != null && placement.Part != null && placement.Part.Components != null && placement.Part.Components.Count == 1)
            {
                return BuildRotatedSingleTemplateCandidates(placement.Part.Components[0].Part.Polygon, 8);
            }
            List<GroupTemplate> templates = new List<GroupTemplate>();
            templates.Add(TemplateFromPlacement(placement));
            return templates;
        }

        private static ShapePartInstance PartForTemplate(ShapePartInstance source, GroupTemplate template)
        {
            if (source == null)
            {
                return null;
            }
            if (source.Components != null && source.Components.Count == 1 && template.Count == 1)
            {
                ShapePartComponent component = source.Components[0];
                ShapePartInstance part = new ShapePartInstance();
                part.TypeIndex = source.TypeIndex;
                part.Name = source.Name;
                part.Number = source.Number;
                part.Polygon = component.Part != null ? component.Part.Polygon : source.Polygon;
                part.ColorIndex = source.ColorIndex;
                part.Components = new List<ShapePartComponent>
                {
                    new ShapePartComponent { Part = component.Part ?? source, LocalPolygon = template.Polygons[0] }
                };
                return part;
            }
            return source;
        }

        private static List<ShapePartComponent> ComponentsInPackingOrder(ShapePlacement placement)
        {
            List<ShapePartComponent> components = new List<ShapePartComponent>(placement.Part.Components);
            components.Sort(delegate (ShapePartComponent a, ShapePartComponent b)
            {
                ShapeBounds ab = TranslateBounds(a.LocalPolygon.Bounds(), placement.X, placement.Y);
                ShapeBounds bb = TranslateBounds(b.LocalPolygon.Bounds(), placement.X, placement.Y);
                int cmp = bb.MaxX.CompareTo(ab.MaxX);
                if (cmp != 0)
                {
                    return cmp;
                }
                return b.LocalPolygon.Area().CompareTo(a.LocalPolygon.Area());
            });
            return components;
        }

        private static List<GroupTemplate> BuildRotatedSingleTemplateCandidates(ShapePolygon polygon, int rotationStep)
        {
            List<GroupTemplate> templates = new List<GroupTemplate>();
            for (int rotation = 0; rotation < 360; rotation += Math.Max(1, rotationStep))
            {
                ShapePolygon p = polygon.RotateNormalize(rotation);
                GroupTemplate template = new GroupTemplate();
                template.Polygons = new List<ShapePolygon> { p };
                template.Bounds = BoundsOf(template.Polygons);
                template.Count = 1;
                templates.Add(template);
            }
            return templates;
        }

        private static ShapePlacement RestoreComponentOnSourceBoard(ShapePartComponent component, int sourceBoard, double originalX, double originalY)
        {
            GroupTemplate template = TemplateFromSingleComponent(component.LocalPolygon);
            ShapePartInstance part = PartFromComponent(component, template);
            ShapeBounds originalBounds = TranslateBounds(component.LocalPolygon.Bounds(), originalX, originalY);
            return MakePlacement(part, template, sourceBoard, originalBounds.MinX, originalBounds.MinY);
        }

        private static GroupTemplate TemplateFromSingleComponent(ShapePolygon polygon)
        {
            GroupTemplate template = new GroupTemplate();
            template.Polygons = NormalizePolygons(new List<ShapePolygon> { polygon });
            template.Bounds = BoundsOf(template.Polygons);
            template.Count = 1;
            return template;
        }

        private static ShapePartInstance PartFromComponent(ShapePartComponent component, GroupTemplate template)
        {
            ShapePartInstance source = component.Part;
            ShapePartInstance group = new ShapePartInstance();
            group.TypeIndex = source.TypeIndex;
            group.Name = source.Name;
            group.Number = source.Number;
            group.Polygon = BoundsPolygon(template.Bounds);
            group.ColorIndex = source.ColorIndex;
            group.Components = new List<ShapePartComponent>
            {
                new ShapePartComponent { Part = source, LocalPolygon = template.Polygons[0] }
            };
            return group;
        }

        private static void RemovePlacement(ShapeNestingResult result, List<ShapePlacement> board, ShapePlacement placement)
        {
            board.Remove(placement);
            result.Placements.Remove(placement);
        }

        private static void RemoveEmptyTrailingBoards(List<List<ShapePlacement>> boards)
        {
            while (boards.Count > 0 && boards[boards.Count - 1].Count == 0)
            {
                boards.RemoveAt(boards.Count - 1);
            }
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

        private static GroupTemplate TemplateFromPlacement(ShapePlacement placement)
        {
            GroupTemplate template = new GroupTemplate();
            template.Polygons = new List<ShapePolygon>();
            foreach (ShapePolygon polygon in ActualPolygons(placement.Part, 0.0, 0.0))
            {
                template.Polygons.Add(polygon);
            }
            template.Polygons = NormalizePolygons(template.Polygons);
            template.Bounds = BoundsOf(template.Polygons);
            template.Count = template.Polygons.Count;
            return template;
        }

        private static ShapePlacement BestPlacementOnBoards(ShapeNestingInput input, List<List<ShapePlacement>> boards, ShapePartInstance groupPart, GroupTemplate template, int firstBoard, int lastBoard)
        {
            return BestPlacementOnBoards(input, boards, groupPart, template, firstBoard, lastBoard, false);
        }

        private static ShapePlacement BestPlacementOnBoards(ShapeNestingInput input, List<List<ShapePlacement>> boards, ShapePartInstance groupPart, GroupTemplate template, int firstBoard, int lastBoard, bool denseBackfill)
        {
            int maxBoard = Math.Min(lastBoard, boards.Count - 1);
            for (int boardIndex = Math.Max(0, firstBoard); boardIndex <= maxBoard; boardIndex++)
            {
                ShapePlacement placement = BestPlacementOnSingleBoard(input, boards, groupPart, template, boardIndex, denseBackfill);
                if (placement != null)
                {
                    return placement;
                }
            }
            return null;
        }

        private static ShapePlacement BestPlacementOnSingleBoard(ShapeNestingInput input, List<List<ShapePlacement>> boards, ShapePartInstance groupPart, GroupTemplate template, int boardIndex)
        {
            return BestPlacementOnSingleBoard(input, boards, groupPart, template, boardIndex, false);
        }

        private static ShapePlacement BestPlacementOnSingleBoard(ShapeNestingInput input, List<List<ShapePlacement>> boards, ShapePartInstance groupPart, GroupTemplate template, int boardIndex, bool denseBackfill)
        {
            ShapePlacement best = null;
            double bestScore = double.PositiveInfinity;
            List<ShapePoint> candidates = CandidatePoints(input, boards[boardIndex], template, denseBackfill);
            foreach (ShapePoint candidate in candidates)
            {
                ShapePlacement placement = MakePlacement(groupPart, template, boardIndex, candidate.X, candidate.Y);
                if (!FitsBoard(input, placement.Bounds))
                {
                    continue;
                }
                if (HitsAny(groupPart, template, candidate.X, candidate.Y, boards[boardIndex]))
                {
                    continue;
                }
                double score = PlacementScore(input, boards[boardIndex], placement, template);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = placement;
                }
            }
            return best;
        }

        private static double PlacementScore(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement placement, GroupTemplate template)
        {
            double usedX = UsedX(board);
            double addedX = Math.Max(0.0, placement.Bounds.MaxX - usedX);
            double near = 0.0;
            double contact = 0.0;
            foreach (ShapePlacement existing in board)
            {
                foreach (ShapePolygon a in ActualPolygons(placement.Part, placement.X, placement.Y))
                {
                    foreach (ShapePolygon b in ActualPolygons(existing.Part, existing.X, existing.Y))
                    {
                        near += ShapeNearReward(a, b, 55.0);
                        contact += BoundsContact(a.Bounds(), b.Bounds(), 8.0);
                    }
                }
            }
            double fillExistingWidthReward = board.Count > 0 && placement.Bounds.MaxX <= usedX + 1e-6 ? input.BoardWidth * 5000000.0 : 0.0;
            double rightVoidReward = board.Count > 0 && placement.Bounds.MinX >= usedX - Math.Max(450.0, template.Bounds.Width * 0.75) ? input.BoardWidth * 3500000.0 : 0.0;
            double upperVoidReward = board.Count > 0 && placement.Bounds.MinY > input.BoardHeight * 0.18 && placement.Bounds.MaxX <= usedX + 420.0 ? input.BoardWidth * 900000.0 : 0.0;
            return addedX * 100000000.0 +
                placement.Bounds.MaxX * 3000.0 +
                placement.Bounds.MaxY * 120.0 +
                placement.Bounds.MinY * 4.0 +
                template.Bounds.Width * template.Bounds.Height * 0.05 -
                fillExistingWidthReward -
                rightVoidReward -
                upperVoidReward -
                near * 600000.0 -
                contact * 220000.0;
        }

        private static List<ShapePoint> CandidatePoints(ShapeNestingInput input, List<ShapePlacement> board, GroupTemplate template, bool denseBackfill)
        {
            List<ShapePoint> points = new List<ShapePoint>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            AddCandidate(points, seen, 0.0, 0.0);
            ShapeBounds tb = template.Bounds;
            double usedX = UsedX(board);
            double usedY = UsedY(board);
            foreach (ShapePlacement existing in board)
            {
                ShapeBounds b = existing.Bounds;
                AddCandidate(points, seen, b.MaxX + Gap, b.MinY);
                AddCandidate(points, seen, b.MinX, b.MaxY + Gap);
                AddCandidate(points, seen, Math.Max(0.0, b.MaxX - tb.Width), b.MinY);
                AddCandidate(points, seen, b.MinX, Math.Max(0.0, b.MaxY - tb.Height));
                AddCandidate(points, seen, Math.Max(0.0, b.MinX - tb.Width - Gap), b.MinY);
                AddCandidate(points, seen, b.MinX, Math.Max(0.0, b.MinY - tb.Height - Gap));

                List<ShapePoint> moving = TemplateAnchorPoints(template);
                foreach (ShapePolygon existingPolygon in ActualPolygons(existing.Part, existing.X, existing.Y))
                {
                    List<ShapePoint> fixedPoints = AnchorPoints(existingPolygon);
                    foreach (ShapePoint fp in fixedPoints)
                    {
                        foreach (ShapePoint mp in moving)
                        {
                            AddCandidate(points, seen, fp.X - mp.X + Gap, fp.Y - mp.Y);
                            AddCandidate(points, seen, fp.X - mp.X - Gap, fp.Y - mp.Y);
                            AddCandidate(points, seen, fp.X - mp.X, fp.Y - mp.Y + Gap);
                            AddCandidate(points, seen, fp.X - mp.X, fp.Y - mp.Y - Gap);
                        }
                    }
                }
            }
            if (board.Count > 0 && usedX > tb.Width + 1e-6)
            {
                double xStep = Math.Max(denseBackfill ? 8.0 : 18.0, tb.Width / (denseBackfill ? 9.0 : 5.0));
                double yStep = Math.Max(denseBackfill ? 8.0 : 18.0, tb.Height / (denseBackfill ? 9.0 : 5.0));
                for (double x = 0.0; x + tb.Width <= usedX + 1e-6; x += xStep)
                {
                    for (double y = 0.0; y + tb.Height <= input.BoardHeight + 1e-6; y += yStep)
                    {
                        AddCandidate(points, seen, x, y);
                    }
                }
            }
            if (board.Count > 0)
            {
                double rightStart = denseBackfill ? 0.0 : Math.Max(0.0, usedX - Math.Max(360.0, tb.Width * 1.35));
                double rightXStep = Math.Max(denseBackfill ? 7.0 : 14.0, tb.Width / (denseBackfill ? 12.0 : 7.0));
                double rightYStep = Math.Max(denseBackfill ? 7.0 : 14.0, tb.Height / (denseBackfill ? 12.0 : 7.0));
                for (double x = rightStart; x + tb.Width <= input.BoardWidth + 1e-6; x += rightXStep)
                {
                    for (double y = 0.0; y + tb.Height <= input.BoardHeight + 1e-6; y += rightYStep)
                    {
                        AddCandidate(points, seen, x, y);
                    }
                }

                double upperStart = denseBackfill ? 0.0 : Math.Max(0.0, usedY - Math.Max(180.0, tb.Height * 1.1));
                double upperXStep = Math.Max(denseBackfill ? 9.0 : 20.0, tb.Width / (denseBackfill ? 10.0 : 5.0));
                double upperYStep = Math.Max(denseBackfill ? 8.0 : 16.0, tb.Height / (denseBackfill ? 10.0 : 6.0));
                double upperLimit = denseBackfill ? Math.Min(input.BoardWidth, usedX + Math.Max(900.0, tb.Width * 2.0)) : Math.Min(input.BoardWidth, usedX + Math.Max(520.0, tb.Width * 1.5));
                for (double x = 0.0; x + tb.Width <= upperLimit + 1e-6; x += upperXStep)
                {
                    for (double y = upperStart; y + tb.Height <= input.BoardHeight + 1e-6; y += upperYStep)
                    {
                        AddCandidate(points, seen, x, y);
                    }
                }

                double xStep = Math.Max(denseBackfill ? 14.0 : 28.0, tb.Width / (denseBackfill ? 7.0 : 4.0));
                double yStep = Math.Max(denseBackfill ? 14.0 : 28.0, tb.Height / (denseBackfill ? 7.0 : 4.0));
                for (double x = 0.0; x + tb.Width <= input.BoardWidth + 1e-6; x += xStep)
                {
                    for (double y = 0.0; y + tb.Height <= input.BoardHeight + 1e-6; y += yStep)
                    {
                        AddCandidate(points, seen, x, y);
                    }
                }

                if (denseBackfill)
                {
                    foreach (ShapePlacement existing in board)
                    {
                        ShapeBounds b = existing.Bounds;
                        double x0 = Math.Max(0.0, b.MinX - tb.Width - Gap * 2.0);
                        double x1 = Math.Min(input.BoardWidth - tb.Width, b.MaxX + Gap * 2.0);
                        double y0 = Math.Max(0.0, b.MinY - tb.Height - Gap * 2.0);
                        double y1 = Math.Min(input.BoardHeight - tb.Height, b.MaxY + Gap * 2.0);
                        double localXStep = Math.Max(6.0, tb.Width / 10.0);
                        double localYStep = Math.Max(6.0, tb.Height / 10.0);
                        for (double x = x0; x <= x1 + 1e-6; x += localXStep)
                        {
                            for (double y = y0; y <= y1 + 1e-6; y += localYStep)
                            {
                                AddCandidate(points, seen, x, y);
                            }
                        }
                    }
                }
            }
            return SortCandidatePoints(points, denseBackfill ? MaxBackfillCandidates : MaxCandidates);
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

        private static string PointKey(double x, double y)
        {
            return ((int)Math.Round(x * 1000.0)).ToString(CultureInfo.InvariantCulture) + "," + ((int)Math.Round(y * 1000.0)).ToString(CultureInfo.InvariantCulture);
        }

        private static void AddSelectedPoint(List<ShapePoint> selected, Dictionary<string, bool> seen, ShapePoint point)
        {
            string key = PointKey(point.X, point.Y);
            if (seen.ContainsKey(key))
            {
                return;
            }
            seen.Add(key, true);
            selected.Add(point);
        }

        private static List<ShapePoint> SortCandidatePoints(List<ShapePoint> points, int limitCount)
        {
            points.Sort(delegate (ShapePoint a, ShapePoint b)
            {
                int cmp = a.X.CompareTo(b.X);
                return cmp != 0 ? cmp : a.Y.CompareTo(b.Y);
            });
            if (points.Count <= limitCount)
            {
                return points;
            }
            List<ShapePoint> selected = new List<ShapePoint>();
            Dictionary<string, bool> selectedSeen = new Dictionary<string, bool>();
            int buckets = Math.Min(32, points.Count);
            for (int bucket = 0; bucket < buckets && selected.Count < limitCount; bucket++)
            {
                int start = (int)Math.Floor(points.Count * bucket / (double)buckets);
                int end = (int)Math.Floor(points.Count * (bucket + 1) / (double)buckets);
                int limit = Math.Min(end, start + Math.Max(1, limitCount / buckets));
                for (int i = start; i < limit && i < points.Count && selected.Count < limitCount; i++)
                {
                    AddSelectedPoint(selected, selectedSeen, points[i]);
                }
            }
            for (int i = 0; i < points.Count && selected.Count < limitCount; i++)
            {
                AddSelectedPoint(selected, selectedSeen, points[i]);
            }
            selected.Sort(delegate (ShapePoint a, ShapePoint b)
            {
                int cmp = a.X.CompareTo(b.X);
                return cmp != 0 ? cmp : a.Y.CompareTo(b.Y);
            });
            return selected;
        }

        private static bool HitsAny(ShapePartInstance groupPart, GroupTemplate template, double x, double y, List<ShapePlacement> board)
        {
            List<ShapePolygon> candidatePolygons = ActualPolygons(groupPart, x, y);
            foreach (ShapePlacement existing in board)
            {
                List<ShapePolygon> existingPolygons = ActualPolygons(existing.Part, existing.X, existing.Y);
                foreach (ShapePolygon candidate in candidatePolygons)
                {
                    ShapeBounds cb = candidate.Bounds();
                    foreach (ShapePolygon placed in existingPolygons)
                    {
                        ShapeBounds pb = placed.Bounds();
                        if (ShapeCollision.BoundsOverlap(cb, pb) && ShapeCollision.Intersects(candidate, cb, placed, pb))
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private static ShapePlacement MakePlacement(ShapePartInstance groupPart, GroupTemplate template, int boardIndex, double x, double y)
        {
            ShapeBounds bounds = TranslateBounds(template.Bounds, x, y);
            ShapePolygon display = BoundsPolygon(bounds);
            return new ShapePlacement
            {
                Part = groupPart,
                BoardIndex = boardIndex,
                X = x,
                Y = y,
                Rotation = 0,
                PlacedPolygon = display,
                Bounds = bounds
            };
        }

        private static ShapePartInstance CreateGroupPart(ShapeNestingInput input, int typeIndex, GroupTemplate template, int[] nextNumber)
        {
            ShapePartType type = input.PartTypes[typeIndex];
            ShapePartInstance group = new ShapePartInstance();
            group.TypeIndex = typeIndex;
            group.Name = type.Name;
            group.Number = nextNumber[typeIndex];
            group.Polygon = BoundsPolygon(template.Bounds);
            group.ColorIndex = type.ColorIndex;
            group.Components = new List<ShapePartComponent>();
            for (int i = 0; i < template.Polygons.Count; i++)
            {
                ShapePartInstance part = new ShapePartInstance
                {
                    TypeIndex = typeIndex,
                    Number = nextNumber[typeIndex]++,
                    Name = type.Name,
                    Polygon = type.Polygon,
                    ColorIndex = type.ColorIndex
                };
                group.Components.Add(new ShapePartComponent { Part = part, LocalPolygon = template.Polygons[i] });
            }
            return group;
        }

        private static List<List<GroupTemplate>> BuildTemplateLibrary(ShapeNestingInput input, Action<string> progress)
        {
            if (progress != null)
            {
                progress("\n" + CodexBridgeVersion.Version + " building hugged group templates...");
                PumpUi(true);
            }
            List<List<GroupTemplate>> library = new List<List<GroupTemplate>>();
            for (int typeIndex = 0; typeIndex < input.PartTypes.Count; typeIndex++)
            {
                PumpUi(false);
                if (TemplateBuildExpired())
                {
                    AddSingleFallbackLibraries(input, library, typeIndex, progress);
                    break;
                }
                List<GroupTemplate> templates = new List<GroupTemplate>();
                ShapePartType type = input.PartTypes[typeIndex];
                AddTemplatesForAllCounts(input, type, templates);
                library.Add(templates);
                if (progress != null)
                {
                    progress("\nV2 type " + type.Name + " templates: " + templates.Count.ToString(CultureInfo.InvariantCulture) + ".");
                }
            }
            return library;
        }

        private static void AddSingleFallbackLibraries(ShapeNestingInput input, List<List<GroupTemplate>> library, int startTypeIndex, Action<string> progress)
        {
            for (int typeIndex = startTypeIndex; typeIndex < input.PartTypes.Count; typeIndex++)
            {
                List<GroupTemplate> templates = new List<GroupTemplate>();
                foreach (int rotation in SingleRotations)
                {
                    AddTemplateIfValid(input, templates, new List<ShapePolygon> { input.PartTypes[typeIndex].Polygon.RotateNormalize(rotation) });
                }
                library.Add(templates);
                if (progress != null)
                {
                    progress("\nV2 type " + input.PartTypes[typeIndex].Name + " templates: " + templates.Count.ToString(CultureInfo.InvariantCulture) + " fallback.");
                }
            }
        }

        private static void AddTemplatesForAllCounts(ShapeNestingInput input, ShapePartType type, List<GroupTemplate> templates)
        {
            PumpUi(false);
            foreach (int rotation in SingleRotations)
            {
                ShapePolygon p = type.Polygon.RotateNormalize(rotation);
                AddTemplateIfValid(input, templates, new List<ShapePolygon> { p });
            }
            if (type.Quantity <= 1 || TemplateBuildExpired())
            {
                return;
            }

            GroupTemplate pair = BestPairTemplate(input, type.Polygon);
            if (pair != null)
            {
                AddTemplateVariants(input, templates, pair.Polygons);
            }

            foreach (int mode in new int[] { 0, 1, 2, 3 })
            {
                if (TemplateBuildExpired())
                {
                    break;
                }
                if (pair != null)
                {
                    AddGrowingSequence(input, type, templates, pair, mode, false, true);
                    AddGrowingSequence(input, type, templates, pair, mode, true, true);
                }
                if (mode == 2 || mode == 3 || pair == null)
                {
                    AddGrowingSequence(input, type, templates, pair, mode, false, false);
                }
            }
            AddMixedDirectionSequences(input, type, templates, pair);
        }

        private static void AddGrowingSequence(ShapeNestingInput input, ShapePartType type, List<GroupTemplate> templates, GroupTemplate pair, int mode, bool pairUnits, bool startWithPair)
        {
            List<ShapePolygon> placed;
            if (startWithPair && pair != null)
            {
                placed = PairSeedPolygons(pair, mode);
            }
            else
            {
                placed = new List<ShapePolygon> { type.Polygon.RotateNormalize(SeedRotationForMode(mode)) };
            }

            placed = NormalizePolygons(placed);
            AddTemplateVariants(input, templates, placed);
            int target = Math.Min(type.Quantity, MaxTemplateGroupCount);
            while (placed.Count < target && !TemplateBuildExpired())
            {
                PumpUi(false);
                bool usePair = pairUnits && pair != null && placed.Count + 2 <= type.Quantity;
                List<List<ShapePolygon>> options = usePair ? PairOptions(pair) : SingleOptions(type.Polygon);
                List<ShapePolygon> moved;
                if (!BestGroupAddition(input, placed, options, mode, out moved))
                {
                    if (usePair && BestGroupAddition(input, placed, SingleOptions(type.Polygon), mode, out moved))
                    {
                        usePair = false;
                    }
                    else
                    {
                        break;
                    }
                }
                placed.AddRange(moved);
                placed = NormalizePolygons(placed);
                AddTemplateVariants(input, templates, placed);
            }
        }

        private static void AddMixedDirectionSequences(ShapeNestingInput input, ShapePartType type, List<GroupTemplate> templates, GroupTemplate pair)
        {
            if (pair == null || type.Quantity < 3)
            {
                return;
            }
            if (TemplateBuildExpired())
            {
                return;
            }

            AddMixedSequence(input, type, templates, pair, PairPolygonsForRotations(pair, new int[] { 0 })[0], false, true, 7);
            AddMixedSequence(input, type, templates, pair, PairPolygonsForRotations(pair, new int[] { 90 })[0], false, false, 7);
            AddMixedSequence(input, type, templates, pair, SingleOptionsForRotations(type.Polygon, new int[] { 90 })[0], true, false, 7);
            AddMixedSequence(input, type, templates, pair, SingleOptionsForRotations(type.Polygon, new int[] { 0 })[0], true, true, 7);

            if (type.Quantity >= 4)
            {
                AddMixedSequence(input, type, templates, pair, PairPolygonsForRotations(pair, new int[] { 0 })[0], true, true, 8);
                AddMixedSequence(input, type, templates, pair, PairPolygonsForRotations(pair, new int[] { 90 })[0], true, false, 8);
            }
        }

        private static void AddMixedSequence(ShapeNestingInput input, ShapePartType type, List<GroupTemplate> templates, GroupTemplate pair, List<ShapePolygon> seed, bool nextPair, bool nextVertical, int mode)
        {
            List<ShapePolygon> placed = NormalizePolygons(seed);
            AddTemplateVariants(input, templates, placed);
            int target = Math.Min(type.Quantity, MaxTemplateGroupCount);
            while (placed.Count < target && !TemplateBuildExpired())
            {
                PumpUi(false);
                bool canAddPair = pair != null && placed.Count + 2 <= type.Quantity;
                List<List<ShapePolygon>> options = nextPair && canAddPair
                    ? PairPolygonsForRotations(pair, nextVertical ? new int[] { 90, 270 } : new int[] { 0, 180 })
                    : SingleOptionsForRotations(type.Polygon, nextVertical ? new int[] { 90, 270 } : new int[] { 0, 180 });

                List<ShapePolygon> moved;
                if (!BestGroupAddition(input, placed, options, mode, out moved))
                {
                    List<List<ShapePolygon>> fallback = canAddPair
                        ? PairPolygonsForRotations(pair, nextVertical ? new int[] { 0, 180 } : new int[] { 90, 270 })
                        : SingleOptions(type.Polygon);
                    if (!BestGroupAddition(input, placed, fallback, mode, out moved))
                    {
                        if (!BestGroupAddition(input, placed, SingleOptions(type.Polygon), mode, out moved))
                        {
                            break;
                        }
                    }
                }

                placed.AddRange(moved);
                placed = NormalizePolygons(placed);
                AddTemplateVariants(input, templates, placed);
                if (nextPair || !canAddPair)
                {
                    nextPair = false;
                }
                else
                {
                    nextPair = true;
                }
                nextVertical = !nextVertical;
            }
        }

        private static int SeedRotationForMode(int mode)
        {
            if (mode == 2 || mode == 5)
            {
                return 90;
            }
            if (mode == 6)
            {
                return 270;
            }
            return 0;
        }

        private static List<ShapePolygon> PairSeedPolygons(GroupTemplate pair, int mode)
        {
            if (mode == 2 || mode == 5)
            {
                return RotateAndNormalize(pair.Polygons, 90);
            }
            if (mode == 6)
            {
                return RotateAndNormalize(pair.Polygons, 270);
            }
            return NormalizePolygons(pair.Polygons);
        }

        private static List<List<ShapePolygon>> SingleOptions(ShapePolygon source)
        {
            return SingleOptionsForRotations(source, SingleRotations);
        }

        private static List<List<ShapePolygon>> SingleOptionsForRotations(ShapePolygon source, int[] rotations)
        {
            List<List<ShapePolygon>> options = new List<List<ShapePolygon>>();
            foreach (int rotation in rotations)
            {
                options.Add(new List<ShapePolygon> { source.RotateNormalize(rotation) });
            }
            return options;
        }

        private static List<List<ShapePolygon>> PairOptions(GroupTemplate pair)
        {
            return PairPolygonsForRotations(pair, new int[] { 0, 90, 180, 270 });
        }

        private static List<List<ShapePolygon>> PairPolygonsForRotations(GroupTemplate pair, int[] rotations)
        {
            List<List<ShapePolygon>> options = new List<List<ShapePolygon>>();
            foreach (int rotation in rotations)
            {
                options.Add(RotateAndNormalize(pair.Polygons, rotation));
            }
            return options;
        }

        private static bool BestGroupAddition(ShapeNestingInput input, List<ShapePolygon> placed, List<List<ShapePolygon>> movingOptions, int mode, out List<ShapePolygon> bestMoved)
        {
            PumpUi(false);
            bestMoved = null;
            double bestScore = double.PositiveInfinity;
            foreach (List<ShapePolygon> option in movingOptions)
            {
                if (TemplateBuildExpired())
                {
                    break;
                }
                List<ShapePolygon> normalized = NormalizePolygons(option);
                List<ShapePoint> candidates = GroupCandidateOffsets(placed, normalized);
                foreach (ShapePoint candidate in candidates)
                {
                    List<ShapePolygon> moved = new List<ShapePolygon>();
                    foreach (ShapePolygon p in normalized)
                    {
                        moved.Add(p.Translate(candidate.X, candidate.Y));
                    }
                    if (GroupPolygonsOverlap(moved, placed))
                    {
                        continue;
                    }
                    List<ShapePolygon> all = new List<ShapePolygon>(placed);
                    all.AddRange(moved);
                    ShapeBounds bounds = BoundsOf(all);
                    if (bounds.Width > input.BoardWidth + 1e-6 || bounds.Height > input.BoardHeight + 1e-6)
                    {
                        continue;
                    }
                    double near = 0.0;
                    double contact = 0.0;
                    foreach (ShapePolygon a in moved)
                    {
                        ShapeBounds ab = a.Bounds();
                        foreach (ShapePolygon b in placed)
                        {
                            near += ShapeNearReward(a, b, 70.0);
                            contact += BoundsContact(ab, b.Bounds(), 8.0);
                        }
                    }
                    double score = PackedGroupScore(bounds, near, contact, mode);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestMoved = moved;
                    }
                }
            }
            return bestMoved != null;
        }

        private static double PackedGroupScore(ShapeBounds bounds, double near, double contact, int mode)
        {
            double area = bounds.Width * bounds.Height;
            if (mode == 1)
            {
                return area + bounds.Height * 1800.0 + bounds.Width * 60.0 - near * 14000.0 - contact * 9000.0;
            }
            if (mode == 2)
            {
                return area + bounds.Width * 1800.0 + bounds.Height * 60.0 - near * 14000.0 - contact * 9000.0;
            }
            if (mode == 3)
            {
                return area + Math.Max(bounds.Width, bounds.Height) * 420.0 + Math.Abs(bounds.Width - bounds.Height) * 120.0 - near * 17000.0 - contact * 11000.0;
            }
            if (mode == 4)
            {
                return area + bounds.Height * 1050.0 + bounds.Width * 180.0 - near * 19000.0 - contact * 12000.0;
            }
            if (mode == 5)
            {
                return area + bounds.Width * 1050.0 + bounds.Height * 180.0 - near * 19000.0 - contact * 12000.0;
            }
            if (mode == 6)
            {
                return area + Math.Max(bounds.Width, bounds.Height) * 260.0 - near * 21000.0 - contact * 13000.0;
            }
            if (mode == 7)
            {
                return area + Math.Abs(bounds.Width - bounds.Height) * 70.0 + Math.Min(bounds.Width, bounds.Height) * 240.0 - near * 23000.0 - contact * 15000.0;
            }
            if (mode == 8)
            {
                return area + Math.Max(bounds.Width, bounds.Height) * 180.0 - near * 25000.0 - contact * 17000.0;
            }
            return area + bounds.Width * 220.0 + bounds.Height * 160.0 - near * 12000.0 - contact * 7000.0;
        }

        private static List<ShapePoint> PairGroupCandidateOffsets(List<ShapePolygon> placed, GroupTemplate pair)
        {
            return GroupCandidateOffsets(placed, pair.Polygons);
        }

        private static List<ShapePoint> GroupCandidateOffsets(List<ShapePolygon> placed, List<ShapePolygon> moving)
        {
            List<ShapePoint> candidates = new List<ShapePoint>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            ShapeBounds mb = BoundsOf(moving);
            List<ShapePoint> movingAnchors = TrimAnchorPoints(PolygonsAnchorPoints(moving), 36);
            foreach (ShapePolygon existing in placed)
            {
                if (candidates.Count >= MaxInternalGroupCandidates)
                {
                    break;
                }
                ShapeBounds b = existing.Bounds();
                AddCandidate(candidates, seen, b.MaxX + Gap - mb.MinX, b.MinY - mb.MinY);
                AddCandidate(candidates, seen, b.MinX - mb.MinX, b.MaxY + Gap - mb.MinY);
                AddCandidate(candidates, seen, Math.Max(0.0, b.MaxX - mb.Width), b.MinY - mb.MinY);
                AddCandidate(candidates, seen, b.MinX - mb.MinX, Math.Max(0.0, b.MaxY - mb.Height));
                AddCandidate(candidates, seen, Math.Max(0.0, b.MinX - mb.Width - Gap), b.MinY - mb.MinY);
                AddCandidate(candidates, seen, b.MinX - mb.MinX, Math.Max(0.0, b.MinY - mb.Height - Gap));

                List<ShapePoint> fixedAnchors = TrimAnchorPoints(AnchorPoints(existing), 18);
                foreach (ShapePoint fixedPoint in fixedAnchors)
                {
                    if (candidates.Count >= MaxInternalGroupCandidates)
                    {
                        break;
                    }
                    foreach (ShapePoint movingPoint in movingAnchors)
                    {
                        AddCandidate(candidates, seen, fixedPoint.X - movingPoint.X + Gap, fixedPoint.Y - movingPoint.Y);
                        AddCandidate(candidates, seen, fixedPoint.X - movingPoint.X - Gap, fixedPoint.Y - movingPoint.Y);
                        AddCandidate(candidates, seen, fixedPoint.X - movingPoint.X, fixedPoint.Y - movingPoint.Y + Gap);
                        AddCandidate(candidates, seen, fixedPoint.X - movingPoint.X, fixedPoint.Y - movingPoint.Y - Gap);
                        if (candidates.Count >= MaxInternalGroupCandidates)
                        {
                            break;
                        }
                    }
                }
            }
            return candidates;
        }

        private static bool GroupPolygonsOverlap(List<ShapePolygon> moving, List<ShapePolygon> placed)
        {
            foreach (ShapePolygon a in moving)
            {
                ShapeBounds ab = a.Bounds();
                foreach (ShapePolygon b in placed)
                {
                    ShapeBounds bb = b.Bounds();
                    if (ShapeCollision.BoundsOverlap(ab, bb) && ShapeCollision.Intersects(a, ab, b, bb))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static void AddTemplateVariants(ShapeNestingInput input, List<GroupTemplate> templates, List<ShapePolygon> polygons)
        {
            foreach (int rotation in new int[] { 0, 90, 180, 270 })
            {
                AddTemplateIfValid(input, templates, RotateAndNormalize(polygons, rotation));
            }
        }

        private static void AddTemplateIfValid(ShapeNestingInput input, List<GroupTemplate> templates, List<ShapePolygon> polygons)
        {
            ShapeBounds bounds = BoundsOf(polygons);
            if (bounds.Width > input.BoardWidth + 1e-6 || bounds.Height > input.BoardHeight + 1e-6)
            {
                return;
            }
            GroupTemplate t = new GroupTemplate();
            t.Polygons = NormalizePolygons(polygons);
            t.Bounds = BoundsOf(t.Polygons);
            t.Count = t.Polygons.Count;
            if (InternalTemplateOverlap(t.Polygons))
            {
                return;
            }
            int sameCount = 0;
            string key = TemplateKey(t);
            foreach (GroupTemplate existing in templates)
            {
                if (existing.Count == t.Count)
                {
                    sameCount++;
                }
                if (TemplateKey(existing) == key)
                {
                    return;
                }
            }
            if (sameCount >= MaxTemplatesPerCount)
            {
                GroupTemplate weakest = null;
                double weakestScore = double.NegativeInfinity;
                foreach (GroupTemplate existing in templates)
                {
                    if (existing.Count != t.Count)
                    {
                        continue;
                    }
                    double score = TemplateCompactness(existing);
                    if (score > weakestScore)
                    {
                        weakestScore = score;
                        weakest = existing;
                    }
                }
                if (weakest != null && TemplateCompactness(t) >= weakestScore)
                {
                    return;
                }
                templates.Remove(weakest);
            }
            templates.Add(t);
        }

        private static string TemplateKey(GroupTemplate template)
        {
            List<string> parts = new List<string>();
            foreach (ShapePolygon polygon in template.Polygons)
            {
                ShapeBounds b = polygon.Bounds();
                parts.Add(((int)Math.Round(b.MinX / 2.0)).ToString(CultureInfo.InvariantCulture) + "," +
                    ((int)Math.Round(b.MinY / 2.0)).ToString(CultureInfo.InvariantCulture) + "," +
                    ((int)Math.Round(b.MaxX / 2.0)).ToString(CultureInfo.InvariantCulture) + "," +
                    ((int)Math.Round(b.MaxY / 2.0)).ToString(CultureInfo.InvariantCulture));
            }
            parts.Sort(StringComparer.Ordinal);
            return template.Count.ToString(CultureInfo.InvariantCulture) + ":" +
                ((int)Math.Round(template.Bounds.Width / 2.0)).ToString(CultureInfo.InvariantCulture) + ":" +
                ((int)Math.Round(template.Bounds.Height / 2.0)).ToString(CultureInfo.InvariantCulture) + ":" +
                string.Join("|", parts.ToArray());
        }

        private static double TemplateCompactness(GroupTemplate template)
        {
            return template.Bounds.Width * template.Bounds.Height + Math.Max(template.Bounds.Width, template.Bounds.Height) * 500.0;
        }

        private static bool InternalTemplateOverlap(List<ShapePolygon> polygons)
        {
            for (int i = 0; i < polygons.Count; i++)
            {
                ShapeBounds aBounds = polygons[i].Bounds();
                for (int j = i + 1; j < polygons.Count; j++)
                {
                    ShapeBounds bBounds = polygons[j].Bounds();
                    if (ShapeCollision.BoundsOverlap(aBounds, bBounds) && ShapeCollision.Intersects(polygons[i], aBounds, polygons[j], bBounds))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static GroupTemplate BestPairTemplate(ShapeNestingInput input, ShapePolygon source)
        {
            GroupTemplate best = null;
            double bestScore = double.PositiveInfinity;
            foreach (int aRotation in SingleRotations)
            {
                ShapePolygon a = source.RotateNormalize(aRotation);
                ShapeBounds ab = a.Bounds();
                foreach (int bRotation in SingleRotations)
                {
                    ShapePolygon bBase = source.RotateNormalize(bRotation);
                    ShapeBounds bb = bBase.Bounds();
                    double step = Math.Max(8.0, Math.Min(ab.Width + bb.Width, ab.Height + bb.Height) / 28.0);
                    for (double x = ab.MinX - bb.MaxX - Gap; x <= ab.MaxX - bb.MinX + Gap + 1e-6; x += step)
                    {
                        for (double y = ab.MinY - bb.MaxY - Gap; y <= ab.MaxY - bb.MinY + Gap + 1e-6; y += step)
                        {
                            ShapePolygon b = bBase.Translate(x, y);
                            ShapeBounds movedB = b.Bounds();
                            if (ShapeCollision.BoundsOverlap(ab, movedB) && ShapeCollision.Intersects(a, ab, b, movedB))
                            {
                                continue;
                            }
                            List<ShapePolygon> normalized = NormalizePolygons(new List<ShapePolygon> { a, b });
                            ShapeBounds bounds = BoundsOf(normalized);
                            if (bounds.Width > input.BoardWidth + 1e-6 || bounds.Height > input.BoardHeight + 1e-6)
                            {
                                continue;
                            }
                            double near = ShapeNearReward(normalized[0], normalized[1], 80.0);
                            double score = bounds.Width * bounds.Height + bounds.Width * 180.0 + bounds.Height * 35.0 - near * 8000.0;
                            if (score < bestScore)
                            {
                                bestScore = score;
                                best = new GroupTemplate { Count = 2, Polygons = normalized, Bounds = bounds };
                            }
                        }
                    }
                }
            }
            return best;
        }

        private static GroupTemplate SelectTemplate(List<GroupTemplate> templates, int count, int variantIndex)
        {
            List<GroupTemplate> matching = new List<GroupTemplate>();
            foreach (GroupTemplate template in templates)
            {
                if (template.Count == count)
                {
                    matching.Add(template);
                }
            }
            if (matching.Count == 0)
            {
                foreach (GroupTemplate template in templates)
                {
                    if (template.Count == 1)
                    {
                        return template;
                    }
                }
                throw new InvalidOperationException("No group template for count " + count.ToString(CultureInfo.InvariantCulture) + ".");
            }
            return matching[Math.Abs(variantIndex) % matching.Count];
        }

        private static int CountTemplates(List<GroupTemplate> templates, int count)
        {
            int total = 0;
            foreach (GroupTemplate template in templates)
            {
                if (template.Count == count)
                {
                    total++;
                }
            }
            return total;
        }

        private static void FinalizeResult(ShapeNestingInput input, ShapeNestingResult result, List<List<ShapePlacement>> boards)
        {
            int boardCount = 0;
            double minRight = double.PositiveInfinity;
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
                double right = input.BoardWidth - usedX;
                minRight = Math.Min(minRight, right);
                totalRight += right;
            }
            result.BoardCount = Math.Max(1, boardCount);
            double lastRight = result.BoardCount <= 0 ? input.BoardWidth : Math.Max(0.0, input.BoardWidth - lastUsedX);
            result.MinRightRemnant = lastRight;
            result.TotalRightRemnant = totalRight;
            result.Utilization = (result.BoardCount * input.BoardWidth - lastRight) / (result.BoardCount * input.BoardWidth);
        }

        private static GroupScore ScoreResult(ShapeNestingResult result)
        {
            if (result == null)
            {
                return null;
            }
            return new GroupScore
            {
                BoardCount = result.BoardCount,
                UsedLengthRatio = result.Utilization,
                TotalRight = result.MinRightRemnant,
                PrefixRight = PrefixRightRemnant(result),
                LoadMoment = BoardLoadMoment(result),
                EnvelopeArea = EnvelopeArea(result)
            };
        }

        private static int CompareScores(GroupScore a, GroupScore b)
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
            int cmp = CompareDescending(a.UsedLengthRatio, b.UsedLengthRatio, 1e-8);
            if (cmp != 0)
            {
                return cmp;
            }
            cmp = CompareAscending(a.PrefixRight, b.PrefixRight, 1e-6);
            if (cmp != 0)
            {
                return cmp;
            }
            cmp = CompareAscending(a.LoadMoment, b.LoadMoment, 1e-6);
            if (cmp != 0)
            {
                return cmp;
            }
            cmp = CompareDescending(a.TotalRight, b.TotalRight, 1e-6);
            if (cmp != 0)
            {
                return cmp;
            }
            return CompareAscending(a.EnvelopeArea, b.EnvelopeArea, 1e-6);
        }

        private static string Format(ShapeNestingResult result)
        {
            return result.BoardCount.ToString(CultureInfo.InvariantCulture) +
                " sheets, used board length " + (result.Utilization * 100.0).ToString("0.00", CultureInfo.InvariantCulture) +
                "%, last right " + result.MinRightRemnant.ToString("0.###", CultureInfo.InvariantCulture) +
                ", total right " + result.TotalRightRemnant.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static double UsedX(List<ShapePlacement> board)
        {
            double used = 0.0;
            foreach (ShapePlacement p in board)
            {
                used = Math.Max(used, p.Bounds.MaxX);
            }
            return used;
        }

        private static double UsedY(List<ShapePlacement> board)
        {
            double used = 0.0;
            foreach (ShapePlacement p in board)
            {
                used = Math.Max(used, p.Bounds.MaxY);
            }
            return used;
        }

        private static double TotalPartArea(ShapeNestingInput input)
        {
            double area = 0.0;
            foreach (ShapePartType type in input.PartTypes)
            {
                area += type.Polygon.Area() * type.Quantity;
            }
            return area;
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

        private static double PrefixRightRemnant(ShapeNestingResult result)
        {
            if (result == null || result.BoardCount <= 1 || result.Input == null)
            {
                return 0.0;
            }
            double[] maxX = new double[Math.Max(1, result.BoardCount)];
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

        private static double BoardLoadMoment(ShapeNestingResult result)
        {
            if (result == null)
            {
                return 0.0;
            }
            double moment = 0.0;
            foreach (ShapePlacement placement in result.Placements)
            {
                double area = 0.0;
                foreach (ShapePolygon polygon in ActualPolygons(placement.Part, 0.0, 0.0))
                {
                    area += polygon.Area();
                }
                moment += area * placement.BoardIndex;
            }
            return moment;
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
                ShapeBounds ab = input.PartTypes[a].Polygon.Bounds();
                ShapeBounds bb = input.PartTypes[b].Polygon.Bounds();
                double aMajor = Math.Max(ab.Width, ab.Height);
                double bMajor = Math.Max(bb.Width, bb.Height);
                int cmp = CompareDescending(aMajor, bMajor, 1e-6);
                if (cmp != 0)
                {
                    return cmp;
                }
                cmp = CompareDescending(input.PartTypes[a].Polygon.Area(), input.PartTypes[b].Polygon.Area(), 1e-6);
                if (cmp != 0)
                {
                    return cmp;
                }
                return a.CompareTo(b);
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

        private static List<ShapePolygon> ActualPolygons(ShapePartInstance part, double x, double y)
        {
            List<ShapePolygon> polygons = new List<ShapePolygon>();
            if (part.Components == null || part.Components.Count == 0)
            {
                polygons.Add(part.Polygon.Translate(x, y));
                return polygons;
            }
            foreach (ShapePartComponent component in part.Components)
            {
                polygons.Add(component.LocalPolygon.Translate(x, y));
            }
            return polygons;
        }

        private static List<ShapePolygon> RotateAndNormalize(List<ShapePolygon> polygons, int rotation)
        {
            double radians = rotation * Math.PI / 180.0;
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);
            List<ShapePolygon> rotated = new List<ShapePolygon>();
            foreach (ShapePolygon polygon in polygons)
            {
                List<ShapePoint> points = new List<ShapePoint>();
                foreach (ShapePoint p in polygon.Points)
                {
                    points.Add(new ShapePoint(p.X * cos - p.Y * sin, p.X * sin + p.Y * cos));
                }
                rotated.Add(new ShapePolygon(points));
            }
            return NormalizePolygons(rotated);
        }

        private static List<ShapePolygon> NormalizePolygons(List<ShapePolygon> polygons)
        {
            ShapeBounds bounds = BoundsOf(polygons);
            List<ShapePolygon> normalized = new List<ShapePolygon>();
            foreach (ShapePolygon polygon in polygons)
            {
                normalized.Add(polygon.Translate(-bounds.MinX, -bounds.MinY));
            }
            return normalized;
        }

        private static ShapeBounds BoundsOf(List<ShapePolygon> polygons)
        {
            ShapeBounds bounds = new ShapeBounds();
            bounds.MinX = double.PositiveInfinity;
            bounds.MinY = double.PositiveInfinity;
            bounds.MaxX = double.NegativeInfinity;
            bounds.MaxY = double.NegativeInfinity;
            foreach (ShapePolygon polygon in polygons)
            {
                ShapeBounds b = polygon.Bounds();
                bounds.MinX = Math.Min(bounds.MinX, b.MinX);
                bounds.MinY = Math.Min(bounds.MinY, b.MinY);
                bounds.MaxX = Math.Max(bounds.MaxX, b.MaxX);
                bounds.MaxY = Math.Max(bounds.MaxY, b.MaxY);
            }
            return bounds;
        }

        private static ShapeBounds TranslateBounds(ShapeBounds bounds, double x, double y)
        {
            return new ShapeBounds { MinX = bounds.MinX + x, MinY = bounds.MinY + y, MaxX = bounds.MaxX + x, MaxY = bounds.MaxY + y };
        }

        private static bool FitsBoard(ShapeNestingInput input, ShapeBounds bounds)
        {
            return bounds.MinX >= -1e-6 && bounds.MinY >= -1e-6 && bounds.MaxX <= input.BoardWidth + 1e-6 && bounds.MaxY <= input.BoardHeight + 1e-6;
        }

        private static ShapePolygon BoundsPolygon(ShapeBounds bounds)
        {
            return new ShapePolygon(new List<ShapePoint>
            {
                new ShapePoint(bounds.MinX, bounds.MinY),
                new ShapePoint(bounds.MaxX, bounds.MinY),
                new ShapePoint(bounds.MaxX, bounds.MaxY),
                new ShapePoint(bounds.MinX, bounds.MaxY)
            });
        }

        private static List<ShapePoint> TemplateAnchorPoints(GroupTemplate template)
        {
            return TrimAnchorPoints(PolygonsAnchorPoints(template.Polygons), 96);
        }

        private static List<ShapePoint> PolygonsAnchorPoints(List<ShapePolygon> polygons)
        {
            List<ShapePoint> anchors = new List<ShapePoint>();
            foreach (ShapePolygon polygon in polygons)
            {
                anchors.AddRange(AnchorPoints(polygon));
            }
            return anchors;
        }

        private static List<ShapePoint> TrimAnchorPoints(List<ShapePoint> anchors, int limit)
        {
            if (anchors.Count <= limit)
            {
                return anchors;
            }
            List<ShapePoint> trimmed = new List<ShapePoint>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            for (int i = 0; i < anchors.Count && trimmed.Count < limit; i += Math.Max(1, anchors.Count / limit))
            {
                AddSelectedPoint(trimmed, seen, anchors[i]);
            }
            for (int i = 0; i < anchors.Count && trimmed.Count < limit; i++)
            {
                AddSelectedPoint(trimmed, seen, anchors[i]);
            }
            return trimmed;
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
            int step = Math.Max(1, polygon.Points.Count / 8);
            for (int i = 0; i < polygon.Points.Count; i += step)
            {
                anchors.Add(polygon.Points[i]);
            }
            return anchors;
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

        private static void SortGenesByPartSizeThenGroup(ShapeNestingInput input, List<GroupGene> genes)
        {
            genes.Sort(delegate (GroupGene a, GroupGene b)
            {
                ShapePartType at = input.PartTypes[a.TypeIndex];
                ShapePartType bt = input.PartTypes[b.TypeIndex];
                ShapeBounds ab = at.Polygon.Bounds();
                ShapeBounds bb = bt.Polygon.Bounds();
                double aMajor = Math.Max(ab.Width, ab.Height);
                double bMajor = Math.Max(bb.Width, bb.Height);
                int cmp = CompareDescending(aMajor, bMajor, 1e-6);
                if (cmp != 0)
                {
                    return cmp;
                }
                double aArea = at.Polygon.Area();
                double bArea = bt.Polygon.Area();
                cmp = CompareDescending(aArea, bArea, 1e-6);
                if (cmp != 0)
                {
                    return cmp;
                }
                double aBox = ab.Width * ab.Height;
                double bBox = bb.Width * bb.Height;
                cmp = CompareDescending(aBox, bBox, 1e-6);
                if (cmp != 0)
                {
                    return cmp;
                }
                cmp = b.Count.CompareTo(a.Count);
                if (cmp != 0)
                {
                    return cmp;
                }
                return a.TypeIndex.CompareTo(b.TypeIndex);
            });
        }

        private static void ShuffleBands(List<GroupGene> genes, Random random, double probability)
        {
            for (int i = 0; i < genes.Count; i++)
            {
                if (random.NextDouble() > probability)
                {
                    continue;
                }
                int j = random.Next(genes.Count);
                GroupGene tmp = genes[i];
                genes[i] = genes[j];
                genes[j] = tmp;
            }
        }

        private static void MutateGeneOrder(List<GroupGene> genes, Random random)
        {
            if (genes == null || genes.Count < 2)
            {
                return;
            }

            int mode = random.Next(4);
            if (mode == 0)
            {
                int a = random.Next(genes.Count);
                int b = random.Next(genes.Count);
                GroupGene tmp = genes[a];
                genes[a] = genes[b];
                genes[b] = tmp;
                return;
            }

            if (mode == 1)
            {
                int from = random.Next(genes.Count);
                GroupGene gene = genes[from];
                genes.RemoveAt(from);
                int to = random.Next(genes.Count + 1);
                genes.Insert(to, gene);
                return;
            }

            if (mode == 2)
            {
                int start = random.Next(genes.Count);
                int end = random.Next(start, genes.Count);
                while (start < end)
                {
                    GroupGene tmp = genes[start];
                    genes[start] = genes[end];
                    genes[end] = tmp;
                    start++;
                    end--;
                }
                return;
            }

            int firstType = genes[random.Next(genes.Count)].TypeIndex;
            List<GroupGene> sameType = new List<GroupGene>();
            for (int i = genes.Count - 1; i >= 0; i--)
            {
                if (genes[i].TypeIndex == firstType)
                {
                    sameType.Add(genes[i]);
                    genes.RemoveAt(i);
                }
            }
            while (sameType.Count > 0)
            {
                int take = random.Next(sameType.Count);
                GroupGene gene = sameType[take];
                sameType.RemoveAt(take);
                genes.Insert(random.Next(genes.Count + 1), gene);
            }
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
                throw new OperationCanceledException("V2 group nesting canceled by Esc.");
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

    internal sealed class GroupTemplate
    {
        public int Count;
        public List<ShapePolygon> Polygons = new List<ShapePolygon>();
        public ShapeBounds Bounds;
    }

    internal struct GroupGene
    {
        public int TypeIndex;
        public int Count;
        public int VariantIndex;
    }

    internal sealed class GroupChromosome
    {
        public List<GroupGene> Genes = new List<GroupGene>();
        public ShapeNestingResult Result;
        public GroupScore Score;
        public bool FreeOrder;

        public GroupChromosome Clone()
        {
            GroupChromosome c = CloneWithoutResult();
            c.Result = Result;
            c.Score = Score;
            return c;
        }

        public GroupChromosome CloneWithoutResult()
        {
            GroupChromosome c = new GroupChromosome();
            c.Genes = new List<GroupGene>(Genes);
            c.FreeOrder = FreeOrder;
            return c;
        }
    }

    internal sealed class GroupScore
    {
        public int BoardCount;
        public double UsedLengthRatio;
        public double TotalRight;
        public double PrefixRight;
        public double LoadMoment;
        public double EnvelopeArea;
    }

    internal sealed class PlacementSnapshot
    {
        public int PlacementCount;
        public int BoardCount;
        public int[] BoardPlacementCounts;
        public int[] NextNumber;
    }
}
