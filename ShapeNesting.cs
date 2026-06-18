using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace CodexAutoCADBridge
{
    internal static class ShapeNestingWorkflow
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

                PromptPointOptions pointOptions = new PromptPointOptions("\nSpecify drawing origin for shape nesting result");
                PromptPointResult pointResult = editor.GetPoint(pointOptions);
                if (pointResult.Status != PromptStatus.OK)
                {
                    return;
                }

                editor.WriteMessage("\nCalculating shape nesting...");
                bool drewBest = false;
                ShapeNestingResult result = ShapeNestingSolver.Solve(input, delegate (string message)
                {
                    editor.WriteMessage(message);
                }, delegate (ShapeNestingResult bestResult)
                {
                    ShapeNestingValidator.Validate(bestResult);
                    ShapeNestingDrawer.Redraw(doc, bestResult, pointResult.Value);
                    drewBest = true;
                });
                ShapeNestingValidator.Validate(result);
                editor.WriteMessage("\nShape nesting result: " + result.BoardCount.ToString(CultureInfo.InvariantCulture) +
                    " sheets, length utilization " + (result.Utilization * 100.0).ToString("0.00", CultureInfo.InvariantCulture) +
                    "%, minimum right remnant " + result.MinRightRemnant.ToString("0.###", CultureInfo.InvariantCulture) +
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
                    editor.WriteMessage("\nCDX_NEST_SHAPE canceled.");
                    return;
                }
                editor.WriteMessage("\nCDX_NEST_SHAPE failed: " + ex.Message);
            }
        }
    }

    internal sealed class ShapeNestingInput
    {
        public double BoardWidth;
        public double BoardHeight;
        public List<ShapePartType> PartTypes = new List<ShapePartType>();
    }

    internal sealed class ShapePartType
    {
        public string Name;
        public ShapePolygon Polygon;
        public int Quantity;
        public short ColorIndex;
    }

    internal sealed class ShapePartInstance
    {
        public int TypeIndex;
        public int Number;
        public string Name;
        public ShapePolygon Polygon;
        public short ColorIndex;
        public List<ShapePartComponent> Components;
    }

    internal sealed class ShapePartComponent
    {
        public ShapePartInstance Part;
        public ShapePolygon LocalPolygon;
    }

    internal sealed class ShapePlacement
    {
        public ShapePartInstance Part;
        public int BoardIndex;
        public double X;
        public double Y;
        public int Rotation;
        public ShapePolygon PlacedPolygon;
        public ShapeBounds Bounds;
        public List<ShapePolygon> ActualPolygons;
        public List<ShapeBounds> ActualPolygonBounds;
    }

    internal sealed class ShapePairTemplate
    {
        public ShapePolygon First;
        public ShapePolygon Second;
        public ShapeBounds Bounds;
        public double Score;
        public int FirstRotation;
        public int SecondRotation;
    }

    internal sealed class StripCursor
    {
        public int BoardIndex;
        public double X;
        public double Y;
        public double ColumnWidth;
    }

    internal sealed class ShapePairPlacement
    {
        public int BoardIndex;
        public double X;
        public double Y;
        public double Score;
        public ShapeBounds Bounds;
        public ShapePairTemplate Template;
    }

    internal sealed class ShapeNestingResult
    {
        public ShapeNestingInput Input;
        public List<ShapePlacement> Placements = new List<ShapePlacement>();
        public int BoardCount;
        public double TotalArea;
        public double Utilization;
        public double MinRightRemnant;
        public double TotalRightRemnant;
        public int Generation = -1;
        public int Individual = -1;
        public string TraceLabel;
    }

    internal struct ShapePoint
    {
        public double X;
        public double Y;

        public ShapePoint(double x, double y)
        {
            X = x;
            Y = y;
        }
    }

    internal sealed class ShapeBounds
    {
        public double MinX;
        public double MinY;
        public double MaxX;
        public double MaxY;

        public double Width
        {
            get { return MaxX - MinX; }
        }

        public double Height
        {
            get { return MaxY - MinY; }
        }
    }

    internal sealed class ShapePolygon
    {
        public readonly List<ShapePoint> Points;

        public ShapePolygon(List<ShapePoint> points)
        {
            Points = points;
        }

        public ShapeBounds Bounds()
        {
            ShapeBounds b = new ShapeBounds();
            b.MinX = double.PositiveInfinity;
            b.MinY = double.PositiveInfinity;
            b.MaxX = double.NegativeInfinity;
            b.MaxY = double.NegativeInfinity;
            foreach (ShapePoint p in Points)
            {
                b.MinX = Math.Min(b.MinX, p.X);
                b.MinY = Math.Min(b.MinY, p.Y);
                b.MaxX = Math.Max(b.MaxX, p.X);
                b.MaxY = Math.Max(b.MaxY, p.Y);
            }
            return b;
        }

        public double Area()
        {
            return Math.Abs(SignedArea()) / 2.0;
        }

        public double SignedArea()
        {
            double sum = 0.0;
            for (int i = 0; i < Points.Count; i++)
            {
                ShapePoint a = Points[i];
                ShapePoint b = Points[(i + 1) % Points.Count];
                sum += a.X * b.Y - b.X * a.Y;
            }
            return sum;
        }

        public ShapePolygon Normalize()
        {
            ShapeBounds b = Bounds();
            List<ShapePoint> pts = new List<ShapePoint>();
            foreach (ShapePoint p in Points)
            {
                pts.Add(new ShapePoint(p.X - b.MinX, p.Y - b.MinY));
            }
            return new ShapePolygon(pts);
        }

        public ShapePolygon Transform(double x, double y, int rotation)
        {
            return RotateNormalize(rotation).Translate(x, y);
        }

        public ShapePolygon RotateNormalize(int rotation)
        {
            double radians = rotation * Math.PI / 180.0;
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);
            List<ShapePoint> raw = new List<ShapePoint>();
            foreach (ShapePoint p in Points)
            {
                raw.Add(new ShapePoint(p.X * cos - p.Y * sin, p.X * sin + p.Y * cos));
            }
            return new ShapePolygon(raw).Normalize();
        }

        public ShapePolygon Translate(double x, double y)
        {
            List<ShapePoint> moved = new List<ShapePoint>();
            foreach (ShapePoint p in Points)
            {
                moved.Add(new ShapePoint(p.X + x, p.Y + y));
            }
            return new ShapePolygon(moved);
        }
    }

    internal static class ShapeExtractor
    {
        private static readonly short[] Colors = new short[] { 30, 3, 4, 2, 5 };
        private static readonly Regex QuantityRegex = new Regex(@"^\s*(\d+)\s*$", RegexOptions.Compiled);

        public static ShapeNestingInput Extract(Document doc)
        {
            List<ExtractedShape> shapes = new List<ExtractedShape>();
            List<QuantityText> quantities = new List<QuantityText>();
            Database db = doc.Database;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                List<CurveSegment> looseSegments = new List<CurveSegment>();

                foreach (ObjectId id in ms)
                {
                    Entity entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity == null)
                    {
                        continue;
                    }
                    if (IsGeneratedNestingEntity(entity))
                    {
                        continue;
                    }

                    Polyline polyline = entity as Polyline;
                    if (polyline != null && polyline.Closed && polyline.NumberOfVertices >= 3)
                    {
                        shapes.Add(new ExtractedShape(ReadPolyline(polyline)));
                        continue;
                    }

                    Circle circle = entity as Circle;
                    if (circle != null)
                    {
                        shapes.Add(new ExtractedShape(ReadCircle(circle, CircleSegments(circle.Radius))));
                        continue;
                    }

                    Line line = entity as Line;
                    if (line != null && line.Length > 1e-6)
                    {
                        looseSegments.Add(ReadLineSegment(line));
                        continue;
                    }

                    Arc arc = entity as Arc;
                    if (arc != null && arc.Length > 1e-6)
                    {
                        looseSegments.Add(ReadArcSegment(arc));
                        continue;
                    }

                    DBText dbText = entity as DBText;
                    if (dbText != null)
                    {
                        AddQuantityText(quantities, dbText.TextString, dbText.Position);
                        continue;
                    }

                    MText mText = entity as MText;
                    if (mText != null)
                    {
                        AddQuantityText(quantities, mText.Contents, mText.Location);
                        continue;
                    }
                }

                shapes.AddRange(BuildLoopsFromSegments(looseSegments));
                tr.Commit();
            }

            if (shapes.Count < 2)
            {
                throw new InvalidOperationException("Need one board outline plus part outlines in the current drawing.");
            }

            shapes.Sort(delegate (ExtractedShape a, ExtractedShape b)
            {
                return b.Polygon.Area().CompareTo(a.Polygon.Area());
            });

            ExtractedShape board = shapes[0];
            ShapeBounds boardBounds = board.Polygon.Bounds();
            List<ExtractedShape> parts = new List<ExtractedShape>();
            for (int i = 1; i < shapes.Count; i++)
            {
                parts.Add(shapes[i]);
            }
            parts.Sort(delegate (ExtractedShape a, ExtractedShape b)
            {
                return a.Polygon.Bounds().MinX.CompareTo(b.Polygon.Bounds().MinX);
            });

            ShapeNestingInput input = new ShapeNestingInput();
            input.BoardWidth = boardBounds.Width;
            input.BoardHeight = boardBounds.Height;
            for (int i = 0; i < parts.Count; i++)
            {
                ShapeBounds partBounds = parts[i].Polygon.Bounds();
                QuantityText quantity = FindQuantityForPart(partBounds, quantities);
                if (quantity == null)
                {
                    continue;
                }
                quantities.Remove(quantity);
                ShapePartType type = new ShapePartType();
                type.Name = "S" + (i + 1).ToString(CultureInfo.InvariantCulture);
                type.Polygon = parts[i].Polygon.Normalize();
                type.Quantity = quantity.Value;
                type.ColorIndex = Colors[i % Colors.Length];
                input.PartTypes.Add(type);
            }

            if (input.PartTypes.Count == 0)
            {
                throw new InvalidOperationException("No numeric quantity text was found below the part outlines.");
            }

            return input;
        }

        private static bool IsGeneratedNestingEntity(Entity entity)
        {
            if (entity == null)
            {
                return false;
            }
            string layer = entity.Layer ?? string.Empty;
            return layer.StartsWith("CDX_NEST_RESULT_", StringComparison.OrdinalIgnoreCase) ||
                layer.StartsWith("CDX_NEST_PREVIEW_", StringComparison.OrdinalIgnoreCase) ||
                layer.StartsWith("SHAPE_NEST_", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layer, "NEST_BOARD", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layer, "NEST_PART", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layer, "NEST_TEXT", StringComparison.OrdinalIgnoreCase);
        }

        private static ShapePolygon ReadPolyline(Polyline polyline)
        {
            List<ShapePoint> pts = new List<ShapePoint>();
            for (int i = 0; i < polyline.NumberOfVertices; i++)
            {
                Point2d p = polyline.GetPoint2dAt(i);
                pts.Add(new ShapePoint(p.X, p.Y));
            }
            return new ShapePolygon(RemoveNearDuplicateClosingPoint(pts));
        }

        private static ShapePolygon ReadCircle(Circle circle, int segments)
        {
            List<ShapePoint> pts = new List<ShapePoint>();
            for (int i = 0; i < segments; i++)
            {
                double a = Math.PI * 2.0 * i / segments;
                pts.Add(new ShapePoint(circle.Center.X + Math.Cos(a) * circle.Radius, circle.Center.Y + Math.Sin(a) * circle.Radius));
            }
            return new ShapePolygon(pts);
        }

        private static int CircleSegments(double radius)
        {
            if (radius <= 15.0)
            {
                return 8;
            }
            if (radius <= 45.0)
            {
                return 10;
            }
            if (radius <= 90.0)
            {
                return 12;
            }
            return 16;
        }

        private static void AddQuantityText(List<QuantityText> quantities, string text, Point3d position)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }
            Match match = QuantityRegex.Match(text.Replace("\\P", "").Trim());
            if (!match.Success)
            {
                return;
            }
            int value;
            if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value <= 0)
            {
                return;
            }
            quantities.Add(new QuantityText { Value = value, Position = new ShapePoint(position.X, position.Y) });
        }

        private static QuantityText FindQuantityForPart(ShapeBounds bounds, List<QuantityText> quantities)
        {
            double centerX = (bounds.MinX + bounds.MaxX) / 2.0;
            double width = Math.Max(bounds.Width, 1.0);
            double height = Math.Max(bounds.Height, 1.0);
            QuantityText best = null;
            double bestScore = double.PositiveInfinity;

            foreach (QuantityText q in quantities)
            {
                double dx = Math.Abs(q.Position.X - centerX);
                double dy = bounds.MinY - q.Position.Y;
                if (dy < -height * 0.15)
                {
                    continue;
                }
                if (dx > width * 0.9 + 250.0)
                {
                    continue;
                }
                if (dy > height * 1.6 + 350.0)
                {
                    continue;
                }
                double score = dx * 2.0 + Math.Abs(dy);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = q;
                }
            }

            return best;
        }

        private static CurveSegment ReadLineSegment(Line line)
        {
            List<ShapePoint> points = new List<ShapePoint>();
            points.Add(new ShapePoint(line.StartPoint.X, line.StartPoint.Y));
            points.Add(new ShapePoint(line.EndPoint.X, line.EndPoint.Y));
            return new CurveSegment(points);
        }

        private static CurveSegment ReadArcSegment(Arc arc)
        {
            double start = arc.StartAngle;
            double end = arc.EndAngle;
            while (end < start)
            {
                end += Math.PI * 2.0;
            }
            int segments = Math.Max(4, (int)Math.Ceiling(Math.Abs(end - start) / (Math.PI / 16.0)));
            List<ShapePoint> points = new List<ShapePoint>();
            for (int i = 0; i <= segments; i++)
            {
                double t = start + (end - start) * i / segments;
                points.Add(new ShapePoint(arc.Center.X + Math.Cos(t) * arc.Radius, arc.Center.Y + Math.Sin(t) * arc.Radius));
            }
            return new CurveSegment(points);
        }

        private static List<ExtractedShape> BuildLoopsFromSegments(List<CurveSegment> segments)
        {
            List<ExtractedShape> loops = new List<ExtractedShape>();
            bool[] used = new bool[segments.Count];
            for (int i = 0; i < segments.Count; i++)
            {
                if (used[i])
                {
                    continue;
                }

                List<ShapePoint> pts = new List<ShapePoint>();
                ShapePoint start = segments[i].Start;
                ShapePoint current = segments[i].End;
                AppendSegmentPoints(pts, segments[i], false);
                used[i] = true;

                bool closed = false;
                for (int guard = 0; guard < segments.Count + 5; guard++)
                {
                    if (Distance(current, start) < 1e-3)
                    {
                        closed = true;
                        break;
                    }

                    bool reverse;
                    int next = FindConnectingSegment(segments, used, current, out reverse);
                    if (next < 0)
                    {
                        break;
                    }

                    used[next] = true;
                    AppendSegmentPoints(pts, segments[next], reverse);
                    current = reverse ? segments[next].Start : segments[next].End;
                }

                if (closed && pts.Count >= 4)
                {
                    loops.Add(new ExtractedShape(new ShapePolygon(RemoveNearDuplicateClosingPoint(pts))));
                }
            }
            return loops;
        }

        private static void AppendSegmentPoints(List<ShapePoint> target, CurveSegment segment, bool reverse)
        {
            if (!reverse)
            {
                for (int i = 0; i < segment.Points.Count; i++)
                {
                    AddPointIfDifferent(target, segment.Points[i]);
                }
                return;
            }
            for (int i = segment.Points.Count - 1; i >= 0; i--)
            {
                AddPointIfDifferent(target, segment.Points[i]);
            }
        }

        private static void AddPointIfDifferent(List<ShapePoint> target, ShapePoint point)
        {
            if (target.Count > 0 && Distance(target[target.Count - 1], point) < 1e-6)
            {
                return;
            }
            target.Add(point);
        }

        private static int FindConnectingSegment(List<CurveSegment> segments, bool[] used, ShapePoint point, out bool reverse)
        {
            int best = -1;
            double bestDistance = 1e-3;
            reverse = false;
            for (int i = 0; i < segments.Count; i++)
            {
                if (used[i])
                {
                    continue;
                }
                double dStart = Distance(segments[i].Start, point);
                if (dStart < bestDistance)
                {
                    bestDistance = dStart;
                    best = i;
                    reverse = false;
                }
                double dEnd = Distance(segments[i].End, point);
                if (dEnd < bestDistance)
                {
                    bestDistance = dEnd;
                    best = i;
                    reverse = true;
                }
            }
            return best;
        }

        private static List<ShapePoint> RemoveNearDuplicateClosingPoint(List<ShapePoint> pts)
        {
            if (pts.Count > 1)
            {
                ShapePoint a = pts[0];
                ShapePoint b = pts[pts.Count - 1];
                if (Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)) < 1e-3)
                {
                    pts.RemoveAt(pts.Count - 1);
                }
            }
            return pts;
        }

        private static double Distance(Point3d a, Point3d b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double Distance(ShapePoint a, ShapePoint b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private sealed class QuantityText
        {
            public int Value;
            public ShapePoint Position;
        }

        private sealed class CurveSegment
        {
            public readonly List<ShapePoint> Points;

            public CurveSegment(List<ShapePoint> points)
            {
                Points = points;
            }

            public ShapePoint Start
            {
                get { return Points[0]; }
            }

            public ShapePoint End
            {
                get { return Points[Points.Count - 1]; }
            }
        }

        private sealed class ExtractedShape
        {
            public readonly ShapePolygon Polygon;

            public ExtractedShape(ShapePolygon polygon)
            {
                Polygon = polygon;
            }
        }
    }

    internal static class ShapeNestingSolver
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        private const int VirtualKeyEscape = 0x1B;
        private const int PopulationSize = 8;
        private const int EliteCount = 2;
        private const int ImmigrantCount = 2;
        private const int Generations = 3;
        private const int MaxCandidates = 160;
        private const int TimeBudgetMs = 2000000;
        private const int ChromosomeBudgetMs = 18000;
        private const int MinimumBoardAttemptMs = 30000;
        private const int TargetBoardAttemptMs = 25000;
        private const int GreedyFallbackBoardAttemptMs = 250;
        private const int GreedyFallbackBoardTryLimit = 8;
        private const double TargetUtilization = 0.40;
        private const double FloorUtilization = 0.25;
        private const double MaxClusterBoxWasteRatio = 2.05;
        private const int PlacementModeDefault = 0;
        private const int PlacementModeLargeHug = 1;
        private const int PlacementModeSmallInsert = 2;
        private const int PlacementModeSmallHug = 3;
        private const double SmallInsertSlack = 35.0;
        private const int HugCandidateLimit = 0;
        private const double HugGap = 1.0;
        private const double HugNearDistance = 80.0;
        private const int UiPumpIntervalMs = 250;
        private const int StaleGenerationLimit = 2;
        private const int LeftCandidateBuckets = 24;
        private const int LeftCandidateDepth = 4;
        private const int FullWidthCandidateSlots = 48;
        private const int FullWidthCandidateDepth = 5;
        private const int GridXLimit = 64;
        private const int GridYLimit = 18;
        private static readonly int[] Rotations = new int[] { 0, 45, 90, 135, 180, 225, 270, 315 };
        private static DateTime _nextUiPump;

        public static ShapeNestingResult Solve(ShapeNestingInput input, Action<string> progress)
        {
            return Solve(input, progress, null);
        }

        public static ShapeNestingResult Solve(ShapeNestingInput input, Action<string> progress, Action<ShapeNestingResult> bestUpdated)
        {
            List<ShapePartInstance> rawParts = Expand(input);
            if (progress != null)
            {
                progress("\nV73 all-seed real placement enabled: fallback seed also uses rotated pair groups and true collision void fill.");
                PumpUi(true);
            }
            return SolvePrepared(input, rawParts, progress, bestUpdated, "single-part large-first", false);
        }

        private static ShapeNestingResult SolvePrepared(ShapeNestingInput input, List<ShapePartInstance> parts, Action<string> progress, Action<ShapeNestingResult> bestUpdated, string passName, bool returnNullWhenNoBest)
        {
            double totalArea = 0.0;
            foreach (ShapePartInstance p in parts)
            {
                totalArea += PartArea(p);
            }
            if (progress != null)
            {
                progress("\nNesting pass: " + passName + ".");
                PumpUi(true);
            }
            WriteEstimate(progress, input, parts.Count, totalArea);

            int seed = unchecked(Environment.TickCount ^ (int)DateTime.UtcNow.Ticks);
            Random random = new Random(seed);
            if (progress != null)
            {
                progress("\nNesting random seed: " + seed.ToString(CultureInfo.InvariantCulture) + ".");
                PumpUi(true);
            }
            List<ShapeChromosome> population = CreatePopulation(parts, random);
            DateTime started = DateTime.UtcNow;
            _nextUiPump = started;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(TimeBudgetMs);
            ShapeChromosome best = null;
            ShapeNestingResult stripSeed = PlaceTypeStripSeed(input, parts, totalArea, true);
            if (!TryAcceptSeed(parts.Count, stripSeed, "Contour pair seed", progress, bestUpdated, out best))
            {
                if (progress != null)
                {
                    progress("\nContour pair seed did not produce a drawable result; retrying conservative pair-strip seed.");
                    PumpUi(true);
                }
                stripSeed = PlaceTypeStripSeed(input, parts, totalArea, false);
                if (!TryAcceptSeed(parts.Count, stripSeed, "Conservative pair-strip seed", progress, bestUpdated, out best))
                {
                    if (progress != null)
                    {
                        progress("\nConservative pair-strip seed also failed; drawing one-part-per-board emergency seed so Gen 0 has a visible baseline.");
                        PumpUi(true);
                    }
                    stripSeed = PlaceOnePartPerBoardSeed(input, parts, totalArea);
                    TryAcceptSeed(parts.Count, stripSeed, "Emergency one-part seed", progress, bestUpdated, out best);
                }
            }

            DateTime evalStarted = DateTime.UtcNow;
            EvalStats stats = Evaluate(population, input, parts, totalArea, deadline, progress, 0, started);
            population.Sort(Compare);
            ShapeChromosome evaluatedBest = BestEvaluated(population);
            if (evaluatedBest != null && Compare(evaluatedBest, best) < 0)
            {
                best = evaluatedBest;
            }
            if (best == null)
            {
                WriteGenerationTime(progress, 0, evalStarted, started, population, stats);
                if (returnNullWhenNoBest)
                {
                    return null;
                }
                throw new InvalidOperationException("No complete placement was found within the utilization floor. Target allows up to " + MaxBoardsForUtilization(input, totalArea, TargetUtilization).ToString(CultureInfo.InvariantCulture) + " sheets; floor allows up to " + MaxBoardsForUtilization(input, totalArea, FloorUtilization).ToString(CultureInfo.InvariantCulture) + " sheets.");
            }
            WriteGenerationTime(progress, 0, evalStarted, started, population, stats);
            WriteProgress(progress, 0, population, best, started);
            NotifyBestUpdated(bestUpdated, best.Result, progress);
            ShapeNestingResult drawnBest = best.Result;

            int staleGenerations = 0;
            for (int g = 0; g < Generations && DateTime.UtcNow <= deadline; g++)
            {
                PumpUi(false);
                List<ShapeChromosome> next = new List<ShapeChromosome>();
                for (int i = 0; i < population.Count && next.Count < EliteCount; i++)
                {
                    if (population[i].Result != null)
                    {
                        next.Add(population[i].Clone());
                    }
                }
                int crossoverTarget = Math.Max(EliteCount, PopulationSize - ImmigrantCount);
                while (next.Count < crossoverTarget && DateTime.UtcNow <= deadline)
                {
                    ShapeChromosome child = Crossover(Tournament(population, random), Tournament(population, random), random, parts);
                    Mutate(child, random, parts);
                    next.Add(child);
                }
                while (next.Count < PopulationSize && DateTime.UtcNow <= deadline)
                {
                    next.Add(RandomImmigrant(parts, random));
                }
                population = next;
                evalStarted = DateTime.UtcNow;
                stats = Evaluate(population, input, parts, totalArea, deadline, progress, g + 1, started);
                population.Sort(Compare);
                ShapeChromosome candidate = BestEvaluated(population);
                if (candidate != null && Compare(candidate, best) < 0)
                {
                    best = candidate;
                    staleGenerations = 0;
                    if (IsVisibleImprovement(best.Result, drawnBest))
                    {
                        NotifyBestUpdated(bestUpdated, best.Result, progress);
                        drawnBest = best.Result;
                    }
                }
                else
                {
                    staleGenerations++;
                }
                WriteGenerationTime(progress, g + 1, evalStarted, started, population, stats);
                WriteProgress(progress, g + 1, population, best, started);
                if (g + 1 >= 6 && staleGenerations >= StaleGenerationLimit)
                {
                    if (progress != null)
                    {
                        progress("\nStopped early after " + staleGenerations.ToString(CultureInfo.InvariantCulture) + " generations without improvement.");
                        PumpUi(true);
                    }
                    break;
                }
            }
            if (progress != null)
            {
                progress("\nBest selected: " + FormatResult(best.Result));
                PumpUi(true);
            }
            return best.Result;
        }

        private static void NotifyBestUpdated(Action<ShapeNestingResult> bestUpdated, ShapeNestingResult result, Action<string> progress)
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
                    progress("\nRedrew visible best: " + FormatResult(result));
                }
            }
            catch (System.Exception ex)
            {
                if (progress != null)
                {
                    progress("\nCurrent best result was not drawn: " + ex.Message);
                }
            }
            PumpUi(true);
        }

        private static bool TryAcceptSeed(int partCount, ShapeNestingResult seed, string label, Action<string> progress, Action<ShapeNestingResult> bestUpdated, out ShapeChromosome chromosome)
        {
            chromosome = null;
            if (seed == null)
            {
                if (progress != null)
                {
                    progress("\n" + label + " was not created.");
                    PumpUi(true);
                }
                return false;
            }
            if (!IsValidResult(seed, progress, label))
            {
                return false;
            }

            ShapeChromosome seedChromosome = NewChromosome(partCount);
            seedChromosome.Result = seed;
            seedChromosome.Fitness = SeedFitness(seed);
            seedChromosome.Generation = 0;
            seedChromosome.Individual = 0;
            seed.Generation = 0;
            seed.Individual = 0;
            chromosome = seedChromosome;
            if (progress != null)
            {
                progress("\n" + label + ": " + FormatResult(seed));
                PumpUi(true);
            }
            NotifyBestUpdated(bestUpdated, seed, progress);
            return true;
        }

        private static bool IsValidResult(ShapeNestingResult result, Action<string> progress, string label)
        {
            try
            {
                ShapeNestingValidator.Validate(result);
                return true;
            }
            catch (System.Exception ex)
            {
                if (progress != null)
                {
                    progress("\n" + label + " rejected: " + ex.Message);
                    PumpUi(true);
                }
                return false;
            }
        }

        private static void WriteProgress(Action<string> progress, int generation, List<ShapeChromosome> population, ShapeChromosome best, DateTime started)
        {
            if (progress == null)
            {
                return;
            }
            ShapeChromosome current = BestEvaluated(population);
            if (current == null || best == null)
            {
                return;
            }
            double seconds = (DateTime.UtcNow - started).TotalSeconds;
            progress("\nGen " + generation.ToString(CultureInfo.InvariantCulture) +
                "  current: " + FormatResult(current.Result) +
                "  best: " + FormatResult(best.Result) +
                "  elapsed: " + seconds.ToString("0.0", CultureInfo.InvariantCulture) + "s");
            PumpUi(true);
        }

        private static string FormatResult(ShapeNestingResult result)
        {
            string source = FormatResultSource(result);
            return source +
                result.BoardCount.ToString(CultureInfo.InvariantCulture) +
                " sheets, length util " + (result.Utilization * 100.0).ToString("0.00", CultureInfo.InvariantCulture) +
                "%, right " + result.MinRightRemnant.ToString("0.###", CultureInfo.InvariantCulture) +
                ", total right " + result.TotalRightRemnant.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string FormatResultSource(ShapeNestingResult result)
        {
            if (result != null && result.Generation >= 0 && result.Individual > 0)
            {
                return "gen " + result.Generation.ToString(CultureInfo.InvariantCulture) +
                    " individual " + result.Individual.ToString(CultureInfo.InvariantCulture) + "  ";
            }
            return string.Empty;
        }

        private static void WriteGenerationTime(Action<string> progress, int generation, DateTime evalStarted, DateTime started, List<ShapeChromosome> population, EvalStats stats)
        {
            if (progress == null)
            {
                return;
            }
            double evalSeconds = (DateTime.UtcNow - evalStarted).TotalSeconds;
            double totalSeconds = (DateTime.UtcNow - started).TotalSeconds;
            progress("\nGen " + generation.ToString(CultureInfo.InvariantCulture) +
                " evaluation done: " + evalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s" +
                ", valid " + CountEvaluated(population).ToString(CultureInfo.InvariantCulture) + "/" + population.Count.ToString(CultureInfo.InvariantCulture) +
                ", new ok " + stats.Completed.ToString(CultureInfo.InvariantCulture) +
                ", skipped " + stats.Skipped.ToString(CultureInfo.InvariantCulture) +
                ", rejected " + stats.Rejected.ToString(CultureInfo.InvariantCulture) +
                ", total " + totalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s");
            PumpUi(true);
        }

        private static List<ShapePartInstance> Expand(ShapeNestingInput input)
        {
            List<ShapePartInstance> parts = new List<ShapePartInstance>();
            for (int t = 0; t < input.PartTypes.Count; t++)
            {
                ShapePartType type = input.PartTypes[t];
                for (int i = 1; i <= type.Quantity; i++)
                {
                    parts.Add(new ShapePartInstance { TypeIndex = t, Number = i, Name = type.Name, Polygon = type.Polygon, ColorIndex = type.ColorIndex });
                }
            }
            return parts;
        }

        private static List<ShapePartInstance> BuildLargePartClusters(ShapeNestingInput input, List<ShapePartInstance> parts, Action<string> progress)
        {
            List<ShapePartInstance> result = new List<ShapePartInstance>();
            Dictionary<int, List<ShapePartInstance>> largeByType = new Dictionary<int, List<ShapePartInstance>>();
            int clusters = 0;
            int rejected = 0;
            foreach (ShapePartInstance part in parts)
            {
                if (PartStage(part) == 0)
                {
                    if (!largeByType.ContainsKey(part.TypeIndex))
                    {
                        largeByType[part.TypeIndex] = new List<ShapePartInstance>();
                    }
                    largeByType[part.TypeIndex].Add(part);
                }
                else
                {
                    result.Add(part);
                }
            }

            foreach (KeyValuePair<int, List<ShapePartInstance>> pair in largeByType)
            {
                List<ShapePartInstance> group = pair.Value;
                group.Sort(delegate (ShapePartInstance a, ShapePartInstance b) { return a.Number.CompareTo(b.Number); });
                int i = 0;
                while (i + 1 < group.Count)
                {
                    ShapePartInstance cluster = CreateLargePairCluster(input, group[i], group[i + 1]);
                    if (cluster == null)
                    {
                        result.Add(group[i]);
                        result.Add(group[i + 1]);
                        rejected++;
                    }
                    else
                    {
                        result.Add(cluster);
                        clusters++;
                    }
                    i += 2;
                }
                if (i < group.Count)
                {
                    result.Add(group[i]);
                }
            }
            if (progress != null)
            {
                progress("\nLarge-part preclusters: " + clusters.ToString(CultureInfo.InvariantCulture) +
                    " accepted, " + rejected.ToString(CultureInfo.InvariantCulture) +
                    " rejected as too loose.");
                PumpUi(true);
            }
            return result;
        }

        private static ShapePartInstance CreateLargePairCluster(ShapeNestingInput input, ShapePartInstance a, ShapePartInstance b)
        {
            ShapePolygon pa = a.Polygon.Normalize();
            ShapePolygon pb = BestMatePolygon(pa, b.Polygon);
            List<ShapePoint> all = new List<ShapePoint>();
            all.AddRange(pa.Points);
            all.AddRange(pb.Points);
            ShapeBounds bounds = new ShapePolygon(all).Bounds();
            ShapePolygon localA = pa.Translate(-bounds.MinX, -bounds.MinY);
            ShapePolygon localB = pb.Translate(-bounds.MinX, -bounds.MinY);
            double boxArea = Math.Max(1.0, bounds.Width * bounds.Height);
            double partArea = Math.Max(1.0, PartArea(a) + PartArea(b));
            if (bounds.Width > input.BoardWidth + 1e-6 ||
                bounds.Height > input.BoardHeight + 1e-6 ||
                boxArea / partArea > MaxClusterBoxWasteRatio)
            {
                return null;
            }
            ShapePolygon box = RectanglePolygon(bounds.Width, bounds.Height);
            ShapePartInstance cluster = new ShapePartInstance();
            cluster.TypeIndex = a.TypeIndex;
            cluster.Number = a.Number;
            cluster.Name = a.Name + "C";
            cluster.Polygon = box;
            cluster.ColorIndex = a.ColorIndex;
            cluster.Components = new List<ShapePartComponent>();
            cluster.Components.Add(new ShapePartComponent { Part = a, LocalPolygon = localA });
            cluster.Components.Add(new ShapePartComponent { Part = b, LocalPolygon = localB });
            return cluster;
        }

        private static ShapePolygon BestMatePolygon(ShapePolygon anchor, ShapePolygon moving)
        {
            ShapePolygon best = null;
            double bestScore = double.PositiveInfinity;
            ShapeBounds ab = anchor.Bounds();
            for (int r = 0; r < Rotations.Length; r++)
            {
                ShapePolygon rotated = moving.RotateNormalize(Rotations[r]);
                ShapeBounds rb = rotated.Bounds();
                List<ShapePoint> offsets = new List<ShapePoint>();
                offsets.Add(new ShapePoint(ab.MaxX - rb.MinX, ab.MinY - rb.MinY));
                offsets.Add(new ShapePoint(ab.MinX - rb.MaxX, ab.MinY - rb.MinY));
                offsets.Add(new ShapePoint(ab.MinX - rb.MinX, ab.MaxY - rb.MinY));
                offsets.Add(new ShapePoint(ab.MinX - rb.MinX, ab.MinY - rb.MaxY));
                offsets.Add(new ShapePoint(ab.MaxX - rb.MinX, ab.MaxY - rb.MaxY));
                offsets.Add(new ShapePoint(ab.MinX - rb.MaxX, ab.MaxY - rb.MaxY));
                foreach (ShapePoint offset in offsets)
                {
                    ShapePolygon candidate = rotated.Translate(offset.X, offset.Y);
                    ShapeBounds cb = candidate.Bounds();
                    if (ShapeCollision.BoundsOverlap(ab, cb) && ShapeCollision.Intersects(anchor, ab, candidate, cb))
                    {
                        continue;
                    }
                    List<ShapePoint> all = new List<ShapePoint>();
                    all.AddRange(anchor.Points);
                    all.AddRange(candidate.Points);
                    ShapeBounds bb = new ShapePolygon(all).Bounds();
                    double contact = ContactLength(ab, cb, 8.0);
                    double score = bb.Width * bb.Height + bb.Width * 180.0 + bb.Height * 40.0 - contact * 8000.0;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = candidate;
                    }
                }
            }
            return best == null ? moving.Normalize().Translate(anchor.Bounds().MaxX, 0.0) : best;
        }

        private static ShapePolygon RectanglePolygon(double width, double height)
        {
            List<ShapePoint> pts = new List<ShapePoint>();
            pts.Add(new ShapePoint(0.0, 0.0));
            pts.Add(new ShapePoint(width, 0.0));
            pts.Add(new ShapePoint(width, height));
            pts.Add(new ShapePoint(0.0, height));
            return new ShapePolygon(pts);
        }

        private static double PartArea(ShapePartInstance part)
        {
            if (part.Components == null || part.Components.Count == 0)
            {
                return part.Polygon.Area();
            }
            double area = 0.0;
            foreach (ShapePartComponent component in part.Components)
            {
                area += component.LocalPolygon.Area();
            }
            return area;
        }

        private static List<ShapeChromosome> CreatePopulation(List<ShapePartInstance> parts, Random random)
        {
            List<ShapeChromosome> population = new List<ShapeChromosome>();
            ShapeChromosome areaFirst = Ordered(parts, LargePartKey);
            population.Add(areaFirst);
            population.Add(Ordered(parts, StageTypeHugKey));
            population.Add(Ordered(parts, delegate (ShapePartInstance p) { return -Math.Max(p.Polygon.Bounds().Width, p.Polygon.Bounds().Height); }));
            population.Add(Ordered(parts, delegate (ShapePartInstance p) { return -p.Polygon.Bounds().Width * p.Polygon.Bounds().Height; }));
            population.Add(Ordered(parts, delegate (ShapePartInstance p) { return -p.Polygon.Bounds().Height; }));
            while (population.Count < PopulationSize)
            {
                ShapeChromosome c = areaFirst.Clone();
                PerturbLargeFirstOrder(c.Order, parts, random);
                for (int i = 0; i < c.RotationGenes.Length; i++)
                {
                    c.RotationGenes[i] = random.Next(Rotations.Length);
                }
                population.Add(c);
            }
            return population;
        }

        private static double LargePartKey(ShapePartInstance p)
        {
            ShapeBounds b = p.Polygon.Bounds();
            return PartStage(p) * 1000000000000.0 - p.Polygon.Area() * 1000000.0 - Math.Max(b.Width, b.Height) * 1000.0 - Math.Min(b.Width, b.Height);
        }

        private static double StageTypeHugKey(ShapePartInstance p)
        {
            ShapeBounds b = p.Polygon.Bounds();
            return PartStage(p) * 1000000000000.0 + p.TypeIndex * 100000000.0 - p.Polygon.Area() * 1000.0 - p.Number;
        }

        private static int PartStage(ShapePartInstance p)
        {
            ShapeBounds b = p.Polygon.Bounds();
            double area = p.Polygon.Area();
            double maxSide = Math.Max(b.Width, b.Height);
            if (area >= 110000.0 || maxSide >= 520.0)
            {
                return 0;
            }
            if (area >= 35000.0 || maxSide >= 260.0)
            {
                return 1;
            }
            return 2;
        }

        private static void PerturbLargeFirstOrder(int[] order, List<ShapePartInstance> parts, Random random)
        {
            int n = order.Length;
            int swaps = Math.Max(2, n / 18);
            for (int s = 0; s < swaps; s++)
            {
                int a = random.Next(n);
                int radius = 4 + random.Next(10);
                int b = Math.Max(0, Math.Min(n - 1, a + random.Next(radius * 2 + 1) - radius));
                if (CanSwapLargeFirst(order[a], order[b], parts))
                {
                    int tmp = order[a];
                    order[a] = order[b];
                    order[b] = tmp;
                }
            }
        }

        private static bool CanSwapLargeFirst(int ia, int ib, List<ShapePartInstance> parts)
        {
            double areaA = parts[ia].Polygon.Area();
            double areaB = parts[ib].Polygon.Area();
            int stageA = PartStage(parts[ia]);
            int stageB = PartStage(parts[ib]);
            if (Math.Abs(stageA - stageB) > 1)
            {
                return false;
            }
            double bigger = Math.Max(areaA, areaB);
            if (bigger < 1e-6)
            {
                return true;
            }
            return Math.Min(areaA, areaB) / bigger > 0.72;
        }

        private static ShapeChromosome Ordered(List<ShapePartInstance> parts, Converter<ShapePartInstance, double> key)
        {
            ShapeChromosome c = NewChromosome(parts.Count);
            List<int> order = new List<int>();
            for (int i = 0; i < parts.Count; i++)
            {
                order.Add(i);
            }
            order.Sort(delegate (int a, int b)
            {
                int cmp = key(parts[a]).CompareTo(key(parts[b]));
                return cmp != 0 ? cmp : a.CompareTo(b);
            });
            c.Order = order.ToArray();
            EnforceStageOrder(c.Order, parts);
            return c;
        }

        private static ShapeChromosome NewChromosome(int count)
        {
            ShapeChromosome c = new ShapeChromosome();
            c.Order = new int[count];
            c.RotationGenes = new int[count];
            for (int i = 0; i < count; i++)
            {
                c.Order[i] = i;
            }
            c.Fitness = double.PositiveInfinity;
            return c;
        }

        private static ShapeChromosome RandomImmigrant(List<ShapePartInstance> parts, Random random)
        {
            ShapeChromosome c = Ordered(parts, LargePartKey);
            PerturbLargeFirstOrder(c.Order, parts, random);
            int n = c.Order.Length;
            int blockMoves = Math.Max(4, n / 35);
            for (int i = 0; i < blockMoves; i++)
            {
                int a = random.Next(n);
                int b = random.Next(n);
                if (Math.Abs(PartStage(parts[c.Order[a]]) - PartStage(parts[c.Order[b]])) > 1)
                {
                    continue;
                }
                int tmp = c.Order[a];
                c.Order[a] = c.Order[b];
                c.Order[b] = tmp;
            }
            if (random.NextDouble() < 0.45)
            {
                ShuffleWithinStages(c.Order, parts, random);
            }
            for (int i = 0; i < c.RotationGenes.Length; i++)
            {
                c.RotationGenes[i] = random.Next(Rotations.Length);
            }
            return c;
        }

        private static EvalStats Evaluate(List<ShapeChromosome> population, ShapeNestingInput input, List<ShapePartInstance> parts, double totalArea, DateTime deadline, Action<string> progress, int generation, DateTime started)
        {
            EvalStats stats = new EvalStats();
            for (int i = 0; i < population.Count; i++)
            {
                ShapeChromosome c = population[i];
                PumpUi(false);
                if (c.Result != null)
                {
                    continue;
                }
                if (DateTime.UtcNow > deadline)
                {
                    break;
                }
                DateTime itemDeadline = MinDate(deadline, DateTime.UtcNow.AddMilliseconds(ChromosomeBudgetMs));
                c.Result = Place(input, parts, c, totalArea, itemDeadline);
                if (c.Result == null)
                {
                    stats.Skipped++;
                    c.Fitness = double.PositiveInfinity;
                    WriteIndividualProgress(progress, generation, i + 1, population.Count, null, started);
                    continue;
                }
                if (c.Result.Utilization > 1.0001)
                {
                    stats.Skipped++;
                    c.Result = null;
                    c.Fitness = double.PositiveInfinity;
                    WriteIndividualProgress(progress, generation, i + 1, population.Count, null, started);
                    continue;
                }
                if (!IsValidResult(c.Result, progress, "Gen " + generation.ToString(CultureInfo.InvariantCulture) + " individual " + (i + 1).ToString(CultureInfo.InvariantCulture)))
                {
                    stats.Skipped++;
                    stats.Rejected++;
                    c.Result = null;
                    c.Fitness = double.PositiveInfinity;
                    WriteIndividualProgressText(progress, generation, i + 1, population.Count, "rejected invalid overlap", started);
                    continue;
                }
                int targetBoards = MaxBoardsForUtilization(input, totalArea, TargetUtilization);
                int floorBoards = MaxBoardsForUtilization(input, totalArea, FloorUtilization);
                if (c.Result.BoardCount > floorBoards)
                {
                    string rejectedText = "rejected below 25% floor: " + FormatResult(c.Result);
                    stats.Skipped++;
                    stats.Rejected++;
                    c.Result = null;
                    c.Fitness = double.PositiveInfinity;
                    WriteIndividualProgressText(progress, generation, i + 1, population.Count, rejectedText, started);
                    continue;
                }
                double usedXMax = MaxUsedX(c.Result);
                double usedXSum = SumUsedXByBoard(c.Result);
                double usedYSum = SumUsedYByBoard(c.Result);
                double envelopeAreaSum = SumEnvelopeAreaByBoard(c.Result);
                double targetPenalty = c.Result.BoardCount > targetBoards ? (c.Result.BoardCount - targetBoards) * 500000000000.0 : 0.0;
                c.Fitness = c.Result.BoardCount * 1000000000000.0 + targetPenalty -
                    c.Result.TotalRightRemnant * 10000000.0 +
                    usedXMax * 100000.0 +
                    usedYSum * 60000.0 +
                    envelopeAreaSum * 2.0;
                c.Generation = generation;
                c.Individual = i + 1;
                c.Result.Generation = generation;
                c.Result.Individual = i + 1;
                stats.Completed++;
                WriteIndividualProgress(progress, generation, i + 1, population.Count, c.Result, started);
            }
            return stats;
        }

        private static DateTime MinDate(DateTime a, DateTime b)
        {
            return a <= b ? a : b;
        }

        private static void WriteIndividualProgress(Action<string> progress, int generation, int index, int count, ShapeNestingResult result, DateTime started)
        {
            WriteIndividualProgressText(progress, generation, index, count, result == null ? "skipped" : FormatResult(result), started);
        }

        private static void WriteIndividualProgressText(Action<string> progress, int generation, int index, int count, string text, DateTime started)
        {
            if (progress == null)
            {
                return;
            }
            double seconds = (DateTime.UtcNow - started).TotalSeconds;
            progress("\nGen " + generation.ToString(CultureInfo.InvariantCulture) +
                " individual " + index.ToString(CultureInfo.InvariantCulture) + "/" + count.ToString(CultureInfo.InvariantCulture) +
                "  " + text +
                "  elapsed: " + seconds.ToString("0.0", CultureInfo.InvariantCulture) + "s");
            PumpUi(true);
        }

        private static ShapeNestingResult Place(ShapeNestingInput input, List<ShapePartInstance> parts, ShapeChromosome c, double totalArea, DateTime deadline)
        {
            int minimumBoards = MinimumBoardCount(input, totalArea);
            int maxTargetBoards = MaxBoardsForUtilization(input, totalArea, TargetUtilization);
            int maxUsefulBoards = MaxBoardsForUtilization(input, totalArea, FloorUtilization);
            ShapeNestingResult best = null;
            int targetLimit = Math.Min(maxUsefulBoards, Math.Max(maxTargetBoards + 2, minimumBoards + 4));
            for (int targetBoards = minimumBoards; targetBoards <= targetLimit; targetBoards++)
            {
                int attemptMs = targetBoards == minimumBoards ? MinimumBoardAttemptMs : TargetBoardAttemptMs;
                DateTime attemptDeadline = MinDate(deadline, DateTime.UtcNow.AddMilliseconds(attemptMs));
                ShapeNestingResult result = PlaceWithTargetBoards(input, parts, c, totalArea, attemptDeadline, targetBoards);
                if (result == null)
                {
                    if (DateTime.UtcNow > deadline)
                    {
                        return null;
                    }
                    continue;
                }
                if (CompareResults(result, best) < 0)
                {
                    best = result;
                }
                if (result.BoardCount <= targetBoards)
                {
                    return result;
                }
            }
            if (DateTime.UtcNow > deadline)
            {
                return best;
            }
            ShapeNestingResult fallback = PlaceGreedyFallback(input, parts, c, totalArea, deadline, minimumBoards, maxUsefulBoards);
            if (CompareResults(fallback, best) < 0)
            {
                return fallback;
            }
            return best;
        }

        private static ShapeNestingResult PlaceWithTargetBoards(ShapeNestingInput input, List<ShapePartInstance> parts, ShapeChromosome c, double totalArea, DateTime deadline, int targetBoards)
        {
            ShapeNestingResult result = new ShapeNestingResult();
            result.Input = input;
            result.TotalArea = totalArea;
            List<List<ShapePlacement>> boards = new List<List<ShapePlacement>>();
            for (int i = 0; i < targetBoards; i++)
            {
                boards.Add(new List<ShapePlacement>());
            }

            foreach (int partIndex in c.Order)
            {
                PumpUi(false);
                if (DateTime.UtcNow > deadline)
                {
                    return null;
                }
                ShapePartInstance part = parts[partIndex];
                ShapePlacement placement = BestPlacementForTarget(input, part, c.RotationGenes[partIndex], boards, deadline, targetBoards);
                if (placement == null && DateTime.UtcNow > deadline)
                {
                    return null;
                }
                if (placement == null)
                {
                    if (boards.Count >= targetBoards)
                    {
                        return null;
                    }
                    boards.Add(new List<ShapePlacement>());
                    placement = FindPlacement(input, part, c.RotationGenes[partIndex], boards.Count - 1, boards[boards.Count - 1], deadline, PlacementModeForPart(part));
                }
                if (placement == null)
                {
                    if (DateTime.UtcNow > deadline)
                    {
                        return null;
                    }
                    throw new InvalidOperationException("Part cannot fit: " + part.Name);
                }
                boards[placement.BoardIndex].Add(placement);
                result.Placements.Add(placement);
            }

            FinalizeResult(result, boards);
            return result;
        }

        private static ShapeNestingResult PlaceGreedyFallback(ShapeNestingInput input, List<ShapePartInstance> parts, ShapeChromosome c, double totalArea, DateTime deadline, int preferredBoards, int maxBoards)
        {
            if (DateTime.UtcNow > deadline)
            {
                return null;
            }
            ShapeNestingResult result = new ShapeNestingResult();
            result.Input = input;
            result.TotalArea = totalArea;
            List<List<ShapePlacement>> boards = new List<List<ShapePlacement>>();

            foreach (int partIndex in c.Order)
            {
                PumpUi(false);
                if (DateTime.UtcNow > deadline)
                {
                    return null;
                }
                ShapePartInstance part = parts[partIndex];
                ShapePlacement placement = null;
                if (DateTime.UtcNow <= deadline)
                {
                    placement = BestGreedyFallbackPlacement(input, part, c.RotationGenes[partIndex], boards, deadline, preferredBoards);
                }
                if (placement == null)
                {
                    if (boards.Count >= maxBoards)
                    {
                        return null;
                    }
                    boards.Add(new List<ShapePlacement>());
                    placement = PlaceOnEmptyBoard(input, part, c.RotationGenes[partIndex], boards.Count - 1);
                }
                if (placement == null)
                {
                    throw new InvalidOperationException("Part cannot fit: " + part.Name);
                }
                boards[placement.BoardIndex].Add(placement);
                result.Placements.Add(placement);
            }

            FinalizeResult(result, boards);
            return result;
        }

        private static ShapeNestingResult PlaceTypeStripSeed(ShapeNestingInput input, List<ShapePartInstance> parts, double totalArea, bool contourPairPlacement)
        {
            List<ShapePartInstance> ordered = new List<ShapePartInstance>(parts);
            ordered.Sort(delegate (ShapePartInstance a, ShapePartInstance b)
            {
                int cmp = PartStage(a).CompareTo(PartStage(b));
                if (cmp != 0)
                {
                    return cmp;
                }
                cmp = a.TypeIndex.CompareTo(b.TypeIndex);
                if (cmp != 0)
                {
                    return cmp;
                }
                return a.Number.CompareTo(b.Number);
            });

            ShapeNestingResult result = new ShapeNestingResult();
            result.Input = input;
            result.TotalArea = totalArea;
            List<List<ShapePlacement>> boards = new List<List<ShapePlacement>>();
            boards.Add(new List<ShapePlacement>());
            double margin = 0.0;
            double gap = 2.0;
            StripCursor cursor = new StripCursor { BoardIndex = 0, X = margin, Y = margin, ColumnWidth = 0.0 };
            int i = 0;
            List<ShapePartInstance> smallParts = new List<ShapePartInstance>();
            List<ShapePartInstance> singleParts = new List<ShapePartInstance>();
            Dictionary<int, ShapePairTemplate> pairTemplates = new Dictionary<int, ShapePairTemplate>();

            while (i < ordered.Count)
            {
                ShapePartInstance first = ordered[i];
                if (PartStage(first) == 2)
                {
                    smallParts.Add(first);
                    i++;
                    continue;
                }
                ShapePartInstance second = i + 1 < ordered.Count && ordered[i + 1].TypeIndex == first.TypeIndex ? ordered[i + 1] : null;
                if (second != null && PartStage(second) == 2)
                {
                    second = null;
                }
                ShapePairTemplate pairTemplate = null;
                if (second != null)
                {
                    if (!pairTemplates.TryGetValue(first.TypeIndex, out pairTemplate))
                    {
                        pairTemplate = BuildPairHugTemplate(first, input);
                        pairTemplates[first.TypeIndex] = pairTemplate;
                    }
                }
                if (pairTemplate != null)
                {
                    PlaceStripPolygonPair(input, result, boards, first, second, pairTemplate, cursor, gap, margin);
                    i += 2;
                }
                else
                {
                    singleParts.Add(first);
                    i++;
                }
                if (i < ordered.Count && ordered[i].TypeIndex != first.TypeIndex)
                {
                    cursor.Y += gap * 8.0;
                }
            }
            PlacePartsIntoVoids(input, result, boards, singleParts, cursor, gap, margin, true, contourPairPlacement);
            PlacePartsIntoVoids(input, result, boards, smallParts, cursor, gap, margin, false, contourPairPlacement);
            FinalizeResult(result, boards);
            return result;
        }

        private static ShapeNestingResult PlaceOnePartPerBoardSeed(ShapeNestingInput input, List<ShapePartInstance> parts, double totalArea)
        {
            List<ShapePartInstance> ordered = new List<ShapePartInstance>(parts);
            ordered.Sort(delegate (ShapePartInstance a, ShapePartInstance b)
            {
                int cmp = PartStage(a).CompareTo(PartStage(b));
                if (cmp != 0)
                {
                    return cmp;
                }
                cmp = a.TypeIndex.CompareTo(b.TypeIndex);
                if (cmp != 0)
                {
                    return cmp;
                }
                return a.Number.CompareTo(b.Number);
            });

            ShapeNestingResult result = new ShapeNestingResult();
            result.Input = input;
            result.TotalArea = totalArea;
            List<List<ShapePlacement>> boards = new List<List<ShapePlacement>>();
            for (int i = 0; i < ordered.Count; i++)
            {
                ShapePartInstance part = ordered[i];
                boards.Add(new List<ShapePlacement>());
                ShapePolygon polygon = BestStripOrientation(part, input);
                AddStripPlacement(result, boards, part, boards.Count - 1, 0.0, 0.0, 0, polygon);
            }
            FinalizeResult(result, boards);
            return result;
        }

        private static void PlacePartsIntoVoids(ShapeNestingInput input, ShapeNestingResult result, List<List<ShapePlacement>> boards, List<ShapePartInstance> parts, StripCursor cursor, double gap, double margin, bool largeSingles, bool allowVoidInsert)
        {
            if (parts.Count == 0)
            {
                return;
            }
            parts.Sort(delegate (ShapePartInstance a, ShapePartInstance b)
            {
                int cmp = PartStage(a).CompareTo(PartStage(b));
                if (cmp != 0)
                {
                    return cmp;
                }
                cmp = a.TypeIndex.CompareTo(b.TypeIndex);
                if (cmp != 0)
                {
                    return cmp;
                }
                return a.Number.CompareTo(b.Number);
            });

            foreach (ShapePartInstance part in parts)
            {
                ShapePlacement placement = BestSeedVoidPlacement(input, part, boards, largeSingles ? PlacementModeForPart(part) : PlacementModeSmallInsert);
                if (placement != null)
                {
                    AddSeedPlacement(result, boards, placement, cursor, gap);
                    continue;
                }
                PlaceSeedPartSafely(input, result, boards, part, cursor, gap);
            }
        }

        private static ShapePlacement BestSeedVoidPlacement(ShapeNestingInput input, ShapePartInstance part, List<List<ShapePlacement>> boards, int placementMode)
        {
            ShapePlacement best = null;
            double bestScore = double.PositiveInfinity;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(largeSearchBudget(placementMode));
            for (int boardIndex = 0; boardIndex < boards.Count; boardIndex++)
            {
                ShapePlacement placement = FindPlacement(input, part, 0, boardIndex, boards[boardIndex], deadline, placementMode);
                if (placement == null)
                {
                    continue;
                }
                double previousUsedX = BoardUsedX(boards[boardIndex]);
                double addedX = Math.Max(0.0, placement.Bounds.MaxX - previousUsedX);
                double existingWidthFillReward = previousUsedX > 1e-6 && placement.Bounds.MaxX <= previousUsedX + 1e-6 ? input.BoardWidth * 800000.0 : 0.0;
                double upperVoidReward = placement.Bounds.MinY > 100.0 && placement.Bounds.MaxX <= previousUsedX + 250.0 ? input.BoardWidth * 250000.0 : 0.0;
                double score = addedX * 16000000.0 +
                    placement.Bounds.MaxX * 700.0 +
                    placement.Bounds.MinY * 0.2 +
                    placement.Bounds.MaxY * 0.1 -
                    existingWidthFillReward -
                    upperVoidReward;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = placement;
                }
            }
            return best;
        }

        private static void PlaceSeedPartSafely(ShapeNestingInput input, ShapeNestingResult result, List<List<ShapePlacement>> boards, ShapePartInstance part, StripCursor cursor, double gap)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(900.0);
            ShapePlacement placement = null;
            double bestScore = double.PositiveInfinity;
            for (int boardIndex = 0; boardIndex < boards.Count; boardIndex++)
            {
                ShapePlacement candidate = FindPlacement(input, part, 0, boardIndex, boards[boardIndex], deadline, PlacementModeForPart(part));
                if (candidate == null)
                {
                    continue;
                }
                double usedX = BoardUsedX(boards[boardIndex]);
                double addedX = Math.Max(0.0, candidate.Bounds.MaxX - usedX);
                double score = addedX * 16000000.0 + candidate.Bounds.MaxX * 700.0 + candidate.Bounds.MaxY;
                if (score < bestScore)
                {
                    bestScore = score;
                    placement = candidate;
                }
            }

            if (placement == null)
            {
                boards.Add(new List<ShapePlacement>());
                placement = PlaceOnEmptyBoard(input, part, 0, boards.Count - 1);
            }
            if (placement == null)
            {
                throw new InvalidOperationException("Part cannot fit: " + part.Name);
            }
            AddSeedPlacement(result, boards, placement, cursor, gap);
        }

        private static void AddSeedPlacement(ShapeNestingResult result, List<List<ShapePlacement>> boards, ShapePlacement placement, StripCursor cursor, double gap)
        {
            boards[placement.BoardIndex].Add(placement);
            result.Placements.Add(placement);
            cursor.BoardIndex = placement.BoardIndex;
            cursor.X = Math.Max(cursor.X, placement.Bounds.MinX);
            cursor.Y = placement.Bounds.MaxY + gap;
            cursor.ColumnWidth = Math.Max(cursor.ColumnWidth, placement.Bounds.Width);
        }

        private static double largeSearchBudget(int placementMode)
        {
            return placementMode == PlacementModeSmallInsert ? 150.0 : 260.0;
        }

        private static void PlaceBoundedStripPolygonPair(ShapeNestingInput input, ShapeNestingResult result, List<List<ShapePlacement>> boards, ShapePartInstance first, ShapePartInstance second, ShapePairTemplate pairTemplate, StripCursor cursor, double gap, double margin)
        {
            ShapeBounds b = pairTemplate.Bounds;
            EnsureStripSpace(input, boards, cursor, b.Width, b.Height, gap, margin);
            ShapePolygon firstPlaced = pairTemplate.First.Translate(cursor.X, cursor.Y);
            ShapePolygon secondPlaced = pairTemplate.Second.Translate(cursor.X, cursor.Y);
            AddStripPlacement(result, boards, first, cursor.BoardIndex, cursor.X, cursor.Y, pairTemplate.FirstRotation, firstPlaced);
            AddStripPlacement(result, boards, second, cursor.BoardIndex, cursor.X, cursor.Y, pairTemplate.SecondRotation, secondPlaced);
            cursor.Y += b.Height + gap;
            cursor.ColumnWidth = Math.Max(cursor.ColumnWidth, b.Width);
        }

        private static void PlaceStripPolygonPair(ShapeNestingInput input, ShapeNestingResult result, List<List<ShapePlacement>> boards, ShapePartInstance first, ShapePartInstance second, ShapePairTemplate pairTemplate, StripCursor cursor, double gap, double margin)
        {
            ShapePairPlacement placement = BestPairStripPlacement(input, boards, pairTemplate, cursor, gap, margin);
            ShapePairTemplate selectedTemplate = placement == null || placement.Template == null ? pairTemplate : placement.Template;
            if (placement == null)
            {
                cursor.BoardIndex++;
                boards.Add(new List<ShapePlacement>());
                cursor.X = margin;
                cursor.Y = margin;
                cursor.ColumnWidth = 0.0;
                placement = new ShapePairPlacement { BoardIndex = cursor.BoardIndex, X = margin, Y = margin, Bounds = selectedTemplate.Bounds, Template = selectedTemplate };
            }

            ShapePolygon firstPlaced = selectedTemplate.First.Translate(placement.X, placement.Y);
            ShapePolygon secondPlaced = selectedTemplate.Second.Translate(placement.X, placement.Y);
            AddStripPlacement(result, boards, first, placement.BoardIndex, placement.X, placement.Y, selectedTemplate.FirstRotation, firstPlaced);
            AddStripPlacement(result, boards, second, placement.BoardIndex, placement.X, placement.Y, selectedTemplate.SecondRotation, secondPlaced);
            cursor.BoardIndex = placement.BoardIndex;
            cursor.X = placement.Bounds.MinX;
            cursor.Y = placement.Bounds.MaxY + gap;
            cursor.ColumnWidth = Math.Max(cursor.ColumnWidth, placement.Bounds.Width);
        }

        private static ShapePairPlacement BestPairStripPlacement(ShapeNestingInput input, List<List<ShapePlacement>> boards, ShapePairTemplate pairTemplate, StripCursor cursor, double gap, double margin)
        {
            ShapePairPlacement best = null;
            List<ShapePairTemplate> templates = PairTemplateVariants(pairTemplate, input);
            foreach (ShapePairTemplate template in templates)
            {
                for (int boardIndex = 0; boardIndex < boards.Count; boardIndex++)
                {
                    List<ShapePoint> candidates = PairCandidatePoints(input, boards[boardIndex], template, cursor, boardIndex, gap, margin);
                    foreach (ShapePoint candidate in candidates)
                    {
                        ShapePairPlacement placement = TryPairPlacement(input, boards[boardIndex], template, boardIndex, candidate.X, candidate.Y);
                        if (placement == null)
                        {
                            continue;
                        }
                        double usedX = BoardUsedX(boards[boardIndex]);
                        double addedX = Math.Max(0.0, placement.Bounds.MaxX - usedX);
                        double shapeNear = PairPlacementNearReward(template, placement.X, placement.Y, boards[boardIndex]);
                        double aboveExistingReward = placement.Bounds.MinY > 120.0 && placement.Bounds.MaxX <= usedX + 350.0 ? 25000000.0 : 0.0;
                        placement.Score = addedX * 10000000.0 +
                            placement.Bounds.MaxX * 1000.0 +
                            placement.Bounds.MinY * 20.0 +
                            placement.Bounds.MaxY -
                            shapeNear * 900000.0 -
                            aboveExistingReward;
                        if (best == null || placement.Score < best.Score)
                        {
                            best = placement;
                        }
                    }
                }
            }
            return best;
        }

        private static List<ShapePoint> PairCandidatePoints(ShapeNestingInput input, List<ShapePlacement> placed, ShapePairTemplate pairTemplate, StripCursor cursor, int boardIndex, double gap, double margin)
        {
            List<ShapePoint> points = new List<ShapePoint>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            AddPairCandidate(points, seen, margin, margin);
            if (boardIndex == cursor.BoardIndex)
            {
                AddPairCandidate(points, seen, cursor.X, cursor.Y);
                AddPairCandidate(points, seen, cursor.X + cursor.ColumnWidth + gap, margin);
            }

            ShapeBounds pb = pairTemplate.Bounds;
            List<ShapePoint> movingAnchors = PairAnchorPoints(pairTemplate);
            foreach (ShapePlacement existing in placed)
            {
                ShapeBounds b = existing.Bounds;
                AddPairCandidate(points, seen, b.MaxX + gap, b.MinY);
                AddPairCandidate(points, seen, b.MinX, b.MaxY + gap);
                AddPairCandidate(points, seen, Math.Max(0.0, b.MaxX - pb.Width), b.MinY);
                AddPairCandidate(points, seen, b.MinX, Math.Max(0.0, b.MaxY - pb.Height));

                List<ShapePoint> fixedAnchors = HugAnchorPoints(existing.PlacedPolygon);
                foreach (ShapePoint fixedPoint in fixedAnchors)
                {
                    foreach (ShapePoint movingPoint in movingAnchors)
                    {
                        AddPairCandidate(points, seen, fixedPoint.X - movingPoint.X + gap, fixedPoint.Y - movingPoint.Y);
                        AddPairCandidate(points, seen, fixedPoint.X - movingPoint.X - gap, fixedPoint.Y - movingPoint.Y);
                        AddPairCandidate(points, seen, fixedPoint.X - movingPoint.X, fixedPoint.Y - movingPoint.Y + gap);
                        AddPairCandidate(points, seen, fixedPoint.X - movingPoint.X, fixedPoint.Y - movingPoint.Y - gap);
                        if (points.Count >= 220)
                        {
                            return points;
                        }
                    }
                }
            }
            return points;
        }

        private static List<ShapePairTemplate> PairTemplateVariants(ShapePairTemplate pairTemplate, ShapeNestingInput input)
        {
            List<ShapePairTemplate> variants = new List<ShapePairTemplate>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            foreach (int rotation in new int[] { 0, 90, 180, 270 })
            {
                ShapePairTemplate variant = RotatePairTemplate(pairTemplate, rotation);
                if (variant.Bounds.Width > input.BoardWidth + 1e-6 || variant.Bounds.Height > input.BoardHeight + 1e-6)
                {
                    continue;
                }
                string key = NormalizeRotation(variant.FirstRotation).ToString(CultureInfo.InvariantCulture) + ":" +
                    NormalizeRotation(variant.SecondRotation).ToString(CultureInfo.InvariantCulture) + ":" +
                    ((int)Math.Round(variant.Bounds.Width * 1000.0)).ToString(CultureInfo.InvariantCulture) + ":" +
                    ((int)Math.Round(variant.Bounds.Height * 1000.0)).ToString(CultureInfo.InvariantCulture);
                if (seen.ContainsKey(key))
                {
                    continue;
                }
                seen.Add(key, true);
                variants.Add(variant);
            }
            variants.Sort(delegate (ShapePairTemplate a, ShapePairTemplate b)
            {
                double areaA = a.Bounds.Width * a.Bounds.Height;
                double areaB = b.Bounds.Width * b.Bounds.Height;
                int cmp = areaA.CompareTo(areaB);
                if (cmp != 0)
                {
                    return cmp;
                }
                return a.Bounds.Height.CompareTo(b.Bounds.Height);
            });
            return variants;
        }

        private static ShapePairTemplate RotatePairTemplate(ShapePairTemplate pairTemplate, int rotation)
        {
            if (rotation == 0)
            {
                ShapePairTemplate clone = new ShapePairTemplate();
                clone.First = pairTemplate.First;
                clone.Second = pairTemplate.Second;
                clone.Bounds = pairTemplate.Bounds;
                clone.Score = pairTemplate.Score;
                clone.FirstRotation = pairTemplate.FirstRotation;
                clone.SecondRotation = pairTemplate.SecondRotation;
                return clone;
            }

            ShapePolygon first = RotateRaw(pairTemplate.First, rotation);
            ShapePolygon second = RotateRaw(pairTemplate.Second, rotation);
            ShapeBounds bounds = BoundsOf(first, second);
            ShapePolygon normalizedFirst = first.Translate(-bounds.MinX, -bounds.MinY);
            ShapePolygon normalizedSecond = second.Translate(-bounds.MinX, -bounds.MinY);
            ShapeBounds normalizedBounds = BoundsOf(normalizedFirst, normalizedSecond);
            return new ShapePairTemplate
            {
                First = normalizedFirst,
                Second = normalizedSecond,
                Bounds = normalizedBounds,
                Score = normalizedBounds.Width * normalizedBounds.Height + normalizedBounds.Width * 260.0 + normalizedBounds.Height * 35.0,
                FirstRotation = NormalizeRotation(pairTemplate.FirstRotation + rotation),
                SecondRotation = NormalizeRotation(pairTemplate.SecondRotation + rotation)
            };
        }

        private static ShapePolygon RotateRaw(ShapePolygon polygon, int rotation)
        {
            double radians = rotation * Math.PI / 180.0;
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);
            List<ShapePoint> points = new List<ShapePoint>();
            foreach (ShapePoint p in polygon.Points)
            {
                points.Add(new ShapePoint(p.X * cos - p.Y * sin, p.X * sin + p.Y * cos));
            }
            return new ShapePolygon(points);
        }

        private static double PairPlacementNearReward(ShapePairTemplate pairTemplate, double x, double y, List<ShapePlacement> placed)
        {
            if (placed.Count == 0)
            {
                return 0.0;
            }
            double reward = 0.0;
            ShapePolygon first = pairTemplate.First.Translate(x, y);
            ShapePolygon second = pairTemplate.Second.Translate(x, y);
            foreach (ShapePlacement existing in placed)
            {
                List<ShapePolygon> existingPolygons = ActualPolygons(existing.Part, existing.PlacedPolygon, existing.X, existing.Y);
                foreach (ShapePolygon existingPolygon in existingPolygons)
                {
                    reward += ShapeNearReward(first, existingPolygon, HugNearDistance);
                    reward += ShapeNearReward(second, existingPolygon, HugNearDistance);
                }
            }
            return reward;
        }

        private static void AddPairCandidate(List<ShapePoint> points, Dictionary<string, bool> seen, double x, double y)
        {
            if (x < -1e-6 || y < -1e-6)
            {
                return;
            }
            ShapePoint p = new ShapePoint(x, y);
            string key = CandidateKey(p);
            if (seen.ContainsKey(key))
            {
                return;
            }
            seen.Add(key, true);
            points.Add(p);
        }

        private static ShapePairPlacement TryPairPlacement(ShapeNestingInput input, List<ShapePlacement> placed, ShapePairTemplate pairTemplate, int boardIndex, double x, double y)
        {
            ShapePolygon first = pairTemplate.First.Translate(x, y);
            ShapePolygon second = pairTemplate.Second.Translate(x, y);
            ShapeBounds bounds = BoundsOf(first, second);
            if (bounds.MinX < -1e-6 || bounds.MinY < -1e-6 || bounds.MaxX > input.BoardWidth + 1e-6 || bounds.MaxY > input.BoardHeight + 1e-6)
            {
                return null;
            }
            foreach (ShapePlacement existing in placed)
            {
                if (PairPolygonOverlaps(first, existing) || PairPolygonOverlaps(second, existing))
                {
                    return null;
                }
            }
            return new ShapePairPlacement { BoardIndex = boardIndex, X = x, Y = y, Bounds = bounds, Template = pairTemplate };
        }

        private static bool PairPolygonOverlaps(ShapePolygon polygon, ShapePlacement existing)
        {
            ShapeBounds bounds = polygon.Bounds();
            if (!ShapeCollision.BoundsOverlap(bounds, existing.Bounds))
            {
                return false;
            }
            List<ShapePolygon> existingPolygons = ActualPolygons(existing.Part, existing.PlacedPolygon, existing.X, existing.Y);
            foreach (ShapePolygon existingPolygon in existingPolygons)
            {
                ShapeBounds existingBounds = existingPolygon.Bounds();
                if (ShapeCollision.BoundsOverlap(bounds, existingBounds) &&
                    ShapeCollision.Intersects(polygon, bounds, existingPolygon, existingBounds))
                {
                    return true;
                }
            }
            return false;
        }

        private static ShapeBounds BoundsOf(ShapePolygon a, ShapePolygon b)
        {
            List<ShapePoint> all = new List<ShapePoint>();
            all.AddRange(a.Points);
            all.AddRange(b.Points);
            return new ShapePolygon(all).Bounds();
        }

        private static List<ShapePoint> PairAnchorPoints(ShapePairTemplate pairTemplate)
        {
            List<ShapePoint> anchors = new List<ShapePoint>();
            anchors.AddRange(HugAnchorPoints(pairTemplate.First));
            anchors.AddRange(HugAnchorPoints(pairTemplate.Second));
            return anchors;
        }

        private static void PlaceStripPolygon(ShapeNestingInput input, ShapeNestingResult result, List<List<ShapePlacement>> boards, ShapePartInstance part, ShapePolygon polygon, StripCursor cursor, double gap, double margin)
        {
            ShapeBounds b = polygon.Bounds();
            EnsureStripSpace(input, boards, cursor, b.Width, b.Height, gap, margin);
            ShapePolygon placed = polygon.Translate(cursor.X, cursor.Y);
            AddStripPlacement(result, boards, part, cursor.BoardIndex, cursor.X, cursor.Y, 0, placed);
            cursor.Y += b.Height + gap;
            cursor.ColumnWidth = Math.Max(cursor.ColumnWidth, b.Width);
        }

        private static void EnsureStripSpace(ShapeNestingInput input, List<List<ShapePlacement>> boards, StripCursor cursor, double width, double height, double gap, double margin)
        {
            if (cursor.Y + height > input.BoardHeight + 1e-6)
            {
                cursor.X += cursor.ColumnWidth + gap;
                cursor.Y = margin;
                cursor.ColumnWidth = 0.0;
            }
            if (cursor.X + width > input.BoardWidth + 1e-6)
            {
                cursor.BoardIndex++;
                boards.Add(new List<ShapePlacement>());
                cursor.X = margin;
                cursor.Y = margin;
                cursor.ColumnWidth = 0.0;
            }
        }

        private static void AddStripPlacement(ShapeNestingResult result, List<List<ShapePlacement>> boards, ShapePartInstance part, int boardIndex, double x, double y, int rotation, ShapePolygon placed)
        {
            ShapePlacement placement = new ShapePlacement
            {
                Part = part,
                BoardIndex = boardIndex,
                X = x,
                Y = y,
                Rotation = rotation,
                PlacedPolygon = placed,
                Bounds = placed.Bounds()
            };
            boards[boardIndex].Add(placement);
            result.Placements.Add(placement);
        }

        private static ShapePolygon BestStripOrientation(ShapePartInstance part, ShapeNestingInput input)
        {
            ShapePolygon best = null;
            double bestScore = double.PositiveInfinity;
            foreach (int rotation in Rotations)
            {
                ShapePolygon p = part.Polygon.RotateNormalize(rotation);
                ShapeBounds b = p.Bounds();
                if (b.Width > input.BoardWidth + 1e-6 || b.Height > input.BoardHeight + 1e-6)
                {
                    continue;
                }
                double score = b.Height * 10000.0 + b.Width;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = p;
                }
            }
            return best == null ? part.Polygon.Normalize() : best;
        }

        private static ShapePairTemplate BuildPairHugTemplate(ShapePartInstance part, ShapeNestingInput input)
        {
            ShapePairTemplate best = null;
            int[] firstRotations = PairRotationCandidates(part.Polygon);
            int[] secondRotations = firstRotations;
            foreach (int firstRotation in firstRotations)
            {
                ShapePolygon first = part.Polygon.RotateNormalize(firstRotation);
                ShapeBounds fb = first.Bounds();
                foreach (int secondRotation in secondRotations)
                {
                    ShapePolygon secondBase = part.Polygon.RotateNormalize(secondRotation);
                    ShapeBounds sb = secondBase.Bounds();
                    List<ShapePoint> offsets = PairSearchOffsets(fb, sb);
                    foreach (ShapePoint offset in offsets)
                    {
                        TryPairOffset(input, first, secondBase, offset, firstRotation, secondRotation, ref best);
                        foreach (ShapePoint refined in RefinePairOffsets(offset, 6.0))
                        {
                            TryPairOffset(input, first, secondBase, refined, firstRotation, secondRotation, ref best);
                        }
                    }
                }
            }
            return best;
        }

        private static int[] PairRotationCandidates(ShapePolygon polygon)
        {
            List<int> rotations = new List<int>();
            AddRotationCandidate(rotations, 0);
            AddRotationCandidate(rotations, 90);
            AddRotationCandidate(rotations, 180);
            AddRotationCandidate(rotations, 270);

            double maxEdge = 0.0;
            for (int i = 0; i < polygon.Points.Count; i++)
            {
                ShapePoint a = polygon.Points[i];
                ShapePoint b = polygon.Points[(i + 1) % polygon.Points.Count];
                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                maxEdge = Math.Max(maxEdge, Math.Sqrt(dx * dx + dy * dy));
            }

            for (int i = 0; i < polygon.Points.Count; i++)
            {
                ShapePoint a = polygon.Points[i];
                ShapePoint b = polygon.Points[(i + 1) % polygon.Points.Count];
                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length < maxEdge * 0.45)
                {
                    continue;
                }
                double angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                AddRotationCandidate(rotations, (int)Math.Round(90.0 - angle));
                AddRotationCandidate(rotations, (int)Math.Round(270.0 - angle));
                AddRotationCandidate(rotations, (int)Math.Round(0.0 - angle));
                AddRotationCandidate(rotations, (int)Math.Round(180.0 - angle));
                if (rotations.Count >= 8)
                {
                    break;
                }
            }
            return rotations.ToArray();
        }

        private static void AddRotationCandidate(List<int> rotations, int rotation)
        {
            int normalized = NormalizeRotation(rotation);
            foreach (int existing in rotations)
            {
                if (Math.Abs(existing - normalized) <= 2 || Math.Abs(existing - normalized) >= 358)
                {
                    return;
                }
            }
            rotations.Add(normalized);
        }

        private static int NormalizeRotation(int rotation)
        {
            int value = rotation % 360;
            if (value < 0)
            {
                value += 360;
            }
            return value;
        }

        private static void TryPairOffset(ShapeNestingInput input, ShapePolygon first, ShapePolygon secondBase, ShapePoint offset, int firstRotation, int secondRotation, ref ShapePairTemplate best)
        {
            ShapeBounds fb = first.Bounds();
            ShapePolygon second = secondBase.Translate(offset.X, offset.Y);
            ShapeBounds secondBounds = second.Bounds();
            if (ShapeCollision.BoundsOverlap(fb, secondBounds) && ShapeCollision.Intersects(first, fb, second, secondBounds))
            {
                return;
            }
            List<ShapePoint> all = new List<ShapePoint>();
            all.AddRange(first.Points);
            all.AddRange(second.Points);
            ShapeBounds bounds = new ShapePolygon(all).Bounds();
            if (bounds.Width > input.BoardWidth + 1e-6 || bounds.Height > input.BoardHeight + 1e-6)
            {
                return;
            }
            ShapePolygon normalizedFirst = first.Translate(-bounds.MinX, -bounds.MinY);
            ShapePolygon normalizedSecond = second.Translate(-bounds.MinX, -bounds.MinY);
            ShapeBounds normalizedBounds = new ShapePolygon(CombinePoints(normalizedFirst, normalizedSecond)).Bounds();
            double score = normalizedBounds.Width * normalizedBounds.Height + normalizedBounds.Width * 260.0 + normalizedBounds.Height * 35.0;
            if (best == null || score < best.Score)
            {
                best = new ShapePairTemplate
                {
                    First = normalizedFirst,
                    Second = normalizedSecond,
                    Bounds = normalizedBounds,
                    Score = score,
                    FirstRotation = firstRotation,
                    SecondRotation = secondRotation
                };
            }
        }

        private static List<ShapePoint> RefinePairOffsets(ShapePoint center, double step)
        {
            List<ShapePoint> offsets = new List<ShapePoint>();
            for (int ix = -1; ix <= 1; ix++)
            {
                for (int iy = -1; iy <= 1; iy++)
                {
                    if (ix == 0 && iy == 0)
                    {
                        continue;
                    }
                    offsets.Add(new ShapePoint(center.X + ix * step, center.Y + iy * step));
                }
            }
            return offsets;
        }

        private static List<ShapePoint> PairSearchOffsets(ShapeBounds firstBounds, ShapeBounds secondBounds)
        {
            List<ShapePoint> offsets = new List<ShapePoint>();
            double xStart = firstBounds.MinX - secondBounds.MaxX - 1.0;
            double xEnd = firstBounds.MaxX - secondBounds.MinX + 1.0;
            double yStart = firstBounds.MinY - secondBounds.MaxY - 1.0;
            double yEnd = firstBounds.MaxY - secondBounds.MinY + 1.0;
            double step = Math.Max(12.0, Math.Min(firstBounds.Width + secondBounds.Width, firstBounds.Height + secondBounds.Height) / 22.0);
            for (double x = xStart; x <= xEnd + 1e-6; x += step)
            {
                for (double y = yStart; y <= yEnd + 1e-6; y += step)
                {
                    offsets.Add(new ShapePoint(x, y));
                }
            }

            offsets.Add(new ShapePoint(firstBounds.MaxX - secondBounds.MinX + 1.0, firstBounds.MinY - secondBounds.MinY));
            offsets.Add(new ShapePoint(firstBounds.MinX - secondBounds.MaxX - 1.0, firstBounds.MinY - secondBounds.MinY));
            offsets.Add(new ShapePoint(firstBounds.MinX - secondBounds.MinX, firstBounds.MaxY - secondBounds.MinY + 1.0));
            offsets.Add(new ShapePoint(firstBounds.MinX - secondBounds.MinX, firstBounds.MinY - secondBounds.MaxY - 1.0));
            offsets.Sort(delegate (ShapePoint a, ShapePoint b)
            {
                double acx = Math.Abs((a.X + secondBounds.MinX + secondBounds.MaxX) / 2.0 - (firstBounds.MinX + firstBounds.MaxX) / 2.0);
                double bcx = Math.Abs((b.X + secondBounds.MinX + secondBounds.MaxX) / 2.0 - (firstBounds.MinX + firstBounds.MaxX) / 2.0);
                int cmp = acx.CompareTo(bcx);
                if (cmp != 0)
                {
                    return cmp;
                }
                double acy = Math.Abs((a.Y + secondBounds.MinY + secondBounds.MaxY) / 2.0 - (firstBounds.MinY + firstBounds.MaxY) / 2.0);
                double bcy = Math.Abs((b.Y + secondBounds.MinY + secondBounds.MaxY) / 2.0 - (firstBounds.MinY + firstBounds.MaxY) / 2.0);
                return acy.CompareTo(bcy);
            });
            return offsets;
        }

        private static List<ShapePoint> CombinePoints(ShapePolygon a, ShapePolygon b)
        {
            List<ShapePoint> all = new List<ShapePoint>();
            all.AddRange(a.Points);
            all.AddRange(b.Points);
            return all;
        }

        private static ShapePlacement BestGreedyFallbackPlacement(ShapeNestingInput input, ShapePartInstance part, int rotationGene, List<List<ShapePlacement>> boards, DateTime deadline, int preferredBoards)
        {
            ShapePlacement best = null;
            double bestScore = double.PositiveInfinity;
            List<int> order = GreedyBoardOrder(boards, preferredBoards);
            foreach (int boardIndex in order)
            {
                if (DateTime.UtcNow > deadline)
                {
                    break;
                }
                DateTime boardDeadline = MinDate(deadline, DateTime.UtcNow.AddMilliseconds(GreedyFallbackBoardAttemptMs));
                ShapePlacement placement = FindPlacement(input, part, rotationGene, boardIndex, boards[boardIndex], boardDeadline, PlacementModeForPart(part));
                if (placement == null)
                {
                    continue;
                }
                double score = TargetBoardScore(input, boards, placement, Math.Max(preferredBoards, boards.Count));
                if (score < bestScore)
                {
                    bestScore = score;
                    best = placement;
                }
            }
            return best;
        }

        private static List<int> GreedyBoardOrder(List<List<ShapePlacement>> boards, int preferredBoards)
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
            int limit = Math.Min(order.Count, Math.Max(GreedyFallbackBoardTryLimit, preferredBoards + 2));
            if (order.Count > limit)
            {
                order.RemoveRange(limit, order.Count - limit);
            }
            return order;
        }

        private static double BoardUsedX(List<ShapePlacement> board)
        {
            double used = 0.0;
            foreach (ShapePlacement p in board)
            {
                used = Math.Max(used, p.Bounds.MaxX);
            }
            return used;
        }

        private static ShapePlacement PlaceOnEmptyBoard(ShapeNestingInput input, ShapePartInstance part, int rotationGene, int boardIndex)
        {
            int rotationCount = part.Components == null || part.Components.Count == 0 ? Rotations.Length : 1;
            for (int r = 0; r < rotationCount; r++)
            {
                int rotation = rotationCount == 1 ? 0 : Rotations[(rotationGene + r) % Rotations.Length];
                ShapePolygon polygon = part.Polygon.RotateNormalize(rotation);
                ShapeBounds bounds = polygon.Bounds();
                if (bounds.MinX >= -1e-6 && bounds.MinY >= -1e-6 && bounds.MaxX <= input.BoardWidth + 1e-6 && bounds.MaxY <= input.BoardHeight + 1e-6)
                {
                    return new ShapePlacement { Part = part, BoardIndex = boardIndex, X = 0.0, Y = 0.0, Rotation = rotation, PlacedPolygon = polygon, Bounds = bounds };
                }
            }
            return null;
        }

        private static void FinalizeResult(ShapeNestingResult result, List<List<ShapePlacement>> boards)
        {
            Dictionary<int, int> remap = new Dictionary<int, int>();
            int next = 0;
            for (int i = 0; i < boards.Count; i++)
            {
                if (boards[i].Count > 0)
                {
                    remap[i] = next;
                    next++;
                }
            }
            foreach (ShapePlacement p in result.Placements)
            {
                if (remap.ContainsKey(p.BoardIndex))
                {
                    p.BoardIndex = remap[p.BoardIndex];
                }
            }
            result.BoardCount = Math.Max(next, 1);
            result.MinRightRemnant = result.Input.BoardWidth - MaxUsedX(result);
            double usedWidthSum = SumUsedXByBoard(result);
            result.TotalRightRemnant = result.Input.BoardWidth * result.BoardCount - usedWidthSum;
            result.Utilization = usedWidthSum / Math.Max(1.0, result.Input.BoardWidth * result.BoardCount);
        }

        private static int MinimumBoardCount(ShapeNestingInput input, double totalArea)
        {
            double boardArea = BoardArea(input);
            return Math.Max(1, (int)Math.Ceiling(totalArea / boardArea - 1e-9));
        }

        private static int MaxBoardsForUtilization(ShapeNestingInput input, double totalArea, double utilization)
        {
            int minimumBoards = MinimumBoardCount(input, totalArea);
            double boardArea = BoardArea(input);
            int maxByUtilization = (int)Math.Floor(totalArea / (boardArea * Math.Max(0.01, utilization)) + 1e-9);
            return Math.Max(minimumBoards, Math.Max(1, maxByUtilization));
        }

        private static double BoardArea(ShapeNestingInput input)
        {
            return Math.Max(1.0, input.BoardWidth * input.BoardHeight);
        }

        private static void WriteEstimate(Action<string> progress, ShapeNestingInput input, int partCount, double totalArea)
        {
            if (progress == null)
            {
                return;
            }
            double boardArea = BoardArea(input);
            int minimumBoards = MinimumBoardCount(input, totalArea);
            int maxTargetBoards = MaxBoardsForUtilization(input, totalArea, TargetUtilization);
            int maxFloorBoards = MaxBoardsForUtilization(input, totalArea, FloorUtilization);
            progress("\nNesting estimate: parts " + partCount.ToString(CultureInfo.InvariantCulture) +
                ", total area " + totalArea.ToString("0.###", CultureInfo.InvariantCulture) +
                ", board area " + boardArea.ToString("0.###", CultureInfo.InvariantCulture) +
                ", full-board theoretical minimum " + minimumBoards.ToString(CultureInfo.InvariantCulture) +
                " sheets, full-board 40% target " + maxTargetBoards.ToString(CultureInfo.InvariantCulture) +
                " sheets, full-board 25% floor " + maxFloorBoards.ToString(CultureInfo.InvariantCulture) + " sheets.");
            PumpUi(true);
        }

        private static ShapePlacement BestPlacementForTarget(ShapeNestingInput input, ShapePartInstance part, int rotationGene, List<List<ShapePlacement>> boards, DateTime deadline, int targetBoards)
        {
            ShapePlacement best = null;
            double bestScore = double.PositiveInfinity;
            for (int b = 0; b < boards.Count; b++)
            {
                ShapePlacement placement = FindPlacement(input, part, rotationGene, b, boards[b], deadline, PlacementModeForPart(part));
                if (placement == null)
                {
                    if (DateTime.UtcNow > deadline)
                    {
                        return null;
                    }
                    continue;
                }
                double score = TargetBoardScore(input, boards, placement, targetBoards);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = placement;
                }
            }
            return best;
        }

        private static double TargetBoardScore(ShapeNestingInput input, List<List<ShapePlacement>> boards, ShapePlacement candidate, int targetBoards)
        {
            double maxUsedX = 0.0;
            double sumUsedX = 0.0;
            double maxUsedY = 0.0;
            double usedBoards = 0.0;
            bool candidateWasEmpty = candidate.BoardIndex >= 0 && candidate.BoardIndex < boards.Count && boards[candidate.BoardIndex].Count == 0;
            for (int b = 0; b < boards.Count; b++)
            {
                double usedX = 0.0;
                double usedY = 0.0;
                bool hasPart = false;
                foreach (ShapePlacement p in boards[b])
                {
                    usedX = Math.Max(usedX, p.Bounds.MaxX);
                    usedY = Math.Max(usedY, p.Bounds.MaxY);
                    hasPart = true;
                }
                if (candidate.BoardIndex == b)
                {
                    usedX = Math.Max(usedX, candidate.Bounds.MaxX);
                    usedY = Math.Max(usedY, candidate.Bounds.MaxY);
                    hasPart = true;
                }
                if (hasPart)
                {
                    usedBoards += 1.0;
                }
                maxUsedX = Math.Max(maxUsedX, usedX);
                sumUsedX += usedX;
                maxUsedY = Math.Max(maxUsedY, usedY);
            }
            double unusedBoardPenalty = Math.Max(0.0, targetBoards - usedBoards) * 2500.0;
            double laterBoardPenalty = candidate.BoardIndex * input.BoardWidth * 50000000.0;
            double emptyBoardPenalty = candidateWasEmpty ? input.BoardWidth * 250000000.0 : 0.0;
            return laterBoardPenalty + emptyBoardPenalty + sumUsedX * 10000000.0 + maxUsedX * 250000.0 + maxUsedY * 250.0 + unusedBoardPenalty;
        }

        private static ShapePlacement FindPlacement(ShapeNestingInput input, ShapePartInstance part, int rotationGene, int boardIndex, List<ShapePlacement> placed, DateTime deadline, int placementMode)
        {
            ShapePlacement best = null;
            double bestScore = double.PositiveInfinity;
            ShapePolygon[] rotated = new ShapePolygon[Rotations.Length];
            int rotationCount = part.Components == null || part.Components.Count == 0 ? Rotations.Length : 1;
            for (int r = 0; r < rotationCount; r++)
            {
                int rotation = rotationCount == 1 ? 0 : Rotations[(rotationGene + r) % Rotations.Length];
                rotated[r] = part.Polygon.RotateNormalize(rotation);
            }

            for (int r = 0; r < rotationCount; r++)
            {
                PumpUi(false);
                if (DateTime.UtcNow > deadline)
                {
                    return null;
                }
                int rotation = rotationCount == 1 ? 0 : Rotations[(rotationGene + r) % Rotations.Length];
                ShapePolygon rotatedPart = rotated[r];
                List<ShapePoint> candidates = CandidatePoints(placed, part, rotatedPart, input, placementMode);
                foreach (ShapePoint candidate in candidates)
                {
                    PumpUi(false);
                    if (DateTime.UtcNow > deadline)
                    {
                        return null;
                    }
                    ShapePolygon polygon = rotatedPart.Translate(candidate.X, candidate.Y);
                    ShapeBounds bounds = polygon.Bounds();
                    if (bounds.MaxX > input.BoardWidth + 1e-6 || bounds.MaxY > input.BoardHeight + 1e-6 || bounds.MinX < -1e-6 || bounds.MinY < -1e-6)
                    {
                        continue;
                    }
                    bool hit = false;
                    List<ShapePlacement> possible = PossibleCollisions(placed, bounds);
                    int collisionChecks = 0;
                    foreach (ShapePlacement existing in possible)
                    {
                        collisionChecks++;
                        if (collisionChecks % 64 == 0 && DateTime.UtcNow > deadline)
                        {
                            return null;
                        }
                        if (collisionChecks % 128 == 0)
                        {
                            PumpUi(false);
                        }
                        if (ShapeCollision.BoundsOverlap(bounds, existing.Bounds) &&
                            ActualPlacementsOverlap(part, polygon, candidate.X, candidate.Y, existing))
                        {
                            hit = true;
                            break;
                        }
                    }
                    if (hit)
                    {
                        continue;
                    }
                    double score = PlacementScore(input, placed, part, polygon, bounds, placementMode);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = new ShapePlacement { Part = part, BoardIndex = boardIndex, X = candidate.X, Y = candidate.Y, Rotation = rotation, PlacedPolygon = polygon, Bounds = bounds };
                    }
                }
            }
            return best;
        }

        private static List<ShapePlacement> PossibleCollisions(List<ShapePlacement> placed, ShapeBounds bounds)
        {
            List<ShapePlacement> possible = new List<ShapePlacement>();
            foreach (ShapePlacement existing in placed)
            {
                if (ShapeCollision.BoundsOverlap(bounds, existing.Bounds))
                {
                    possible.Add(existing);
                }
            }
            return possible;
        }

        private static bool ActualPlacementsOverlap(ShapePartInstance part, ShapePolygon polygon, double x, double y, ShapePlacement existing)
        {
            List<ShapePolygon> candidatePolygons = ActualPolygons(part, polygon, x, y);
            List<ShapePolygon> existingPolygons = ActualPolygons(existing.Part, existing.PlacedPolygon, existing.X, existing.Y);
            foreach (ShapePolygon candidate in candidatePolygons)
            {
                ShapeBounds candidateBounds = candidate.Bounds();
                foreach (ShapePolygon placed in existingPolygons)
                {
                    ShapeBounds placedBounds = placed.Bounds();
                    if (ShapeCollision.BoundsOverlap(candidateBounds, placedBounds) &&
                        ShapeCollision.Intersects(candidate, candidateBounds, placed, placedBounds))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static List<ShapePolygon> ActualPolygons(ShapePartInstance part, ShapePolygon polygon, double x, double y)
        {
            List<ShapePolygon> polygons = new List<ShapePolygon>();
            if (part.Components == null || part.Components.Count == 0)
            {
                polygons.Add(polygon);
                return polygons;
            }
            foreach (ShapePartComponent component in part.Components)
            {
                polygons.Add(component.LocalPolygon.Translate(x, y));
            }
            return polygons;
        }

        private static void PumpUi(bool force)
        {
            if ((GetAsyncKeyState(VirtualKeyEscape) & 0x8000) != 0)
            {
                throw new OperationCanceledException("Shape nesting canceled by Esc.");
            }
            DateTime now = DateTime.UtcNow;
            if (!force && now < _nextUiPump)
            {
                return;
            }
            _nextUiPump = now.AddMilliseconds(UiPumpIntervalMs);
            try
            {
                System.Windows.Forms.Application.DoEvents();
            }
            catch
            {
            }
        }

        private static int PlacementModeForPart(ShapePartInstance part)
        {
            int stage = PartStage(part);
            if (stage == 0)
            {
                return PlacementModeLargeHug;
            }
            if (stage == 2)
            {
                return PlacementModeSmallInsert;
            }
            return PlacementModeSmallHug;
        }

        private static double PlacementScore(ShapeNestingInput input, List<ShapePlacement> placed, ShapePartInstance part, ShapePolygon candidatePolygon, ShapeBounds candidate, int placementMode)
        {
            double maxX = candidate.MaxX;
            double maxY = candidate.MaxY;
            double previousMaxX = 0.0;
            double previousMaxY = 0.0;
            double minGap = double.PositiveInfinity;
            double contact = 0.0;
            double nearContact = 0.0;
            double enclosedGapPenalty = 0.0;
            double sameTypeContact = 0.0;
            double sameStageContact = 0.0;
            double sameTypeNear = 0.0;
            double sameTypeShapeNear = 0.0;
            double sameStageShapeNear = 0.0;
            foreach (ShapePlacement existing in placed)
            {
                previousMaxX = Math.Max(previousMaxX, existing.Bounds.MaxX);
                previousMaxY = Math.Max(previousMaxY, existing.Bounds.MaxY);
                maxX = Math.Max(maxX, existing.Bounds.MaxX);
                maxY = Math.Max(maxY, existing.Bounds.MaxY);
                double gap = GapBetween(candidate, existing.Bounds);
                minGap = Math.Min(minGap, gap);
                double contactLength = ContactLength(candidate, existing.Bounds, 10.0);
                contact += contactLength;
                if (gap < 45.0)
                {
                    nearContact += 45.0 - gap;
                }
                if (existing.Part.TypeIndex == part.TypeIndex)
                {
                    sameTypeContact += contactLength;
                    if (gap < 90.0)
                    {
                        sameTypeNear += 90.0 - gap;
                    }
                    sameTypeShapeNear += ShapeNearReward(candidatePolygon, existing.PlacedPolygon, HugNearDistance);
                }
                else if (PartStage(existing.Part) == PartStage(part))
                {
                    sameStageContact += contactLength;
                    sameStageShapeNear += ShapeNearReward(candidatePolygon, existing.PlacedPolygon, HugNearDistance);
                }
                enclosedGapPenalty += NarrowVoidPenalty(candidate, existing.Bounds);
            }

            double addedX = Math.Max(0.0, candidate.MaxX - previousMaxX);
            double envelopeArea = maxX * maxY;
            double compactX = maxX * 220000.0 + addedX * 650000.0;
            double compactArea = envelopeArea * 7.0;
            double compactY = maxY * 1800.0;
            double lowLeftTieBreak = candidate.MinX * 30.0 + candidate.MinY * 6.0;
            double isolatedPenalty = placed.Count == 0 ? 0.0 : Math.Min(minGap, 260.0) * 160000.0;
            double contactReward = contact * 420000.0 + nearContact * 85000.0;
            double holePenalty = enclosedGapPenalty * 18000.0;
            double score = compactX + compactArea + compactY + lowLeftTieBreak + isolatedPenalty + holePenalty - contactReward;

            if (placementMode == PlacementModeLargeHug)
            {
                score -= sameTypeContact * 1700000.0;
                score -= sameStageContact * 620000.0;
                score -= sameTypeNear * 260000.0;
                score -= sameTypeShapeNear * 900000.0;
                score -= sameStageShapeNear * 320000.0;
                score += Math.Max(0.0, candidate.MinX - previousMaxX - 80.0) * 380000.0;
            }
            else if (placementMode == PlacementModeSmallInsert)
            {
                if (placed.Count > 0 && candidate.MaxX <= previousMaxX + SmallInsertSlack && candidate.MaxY <= input.BoardHeight + 1e-6)
                {
                    score -= input.BoardWidth * 900000.0;
                }
                score += addedX * 1600000.0;
                score -= sameTypeContact * 850000.0;
                score -= sameTypeNear * 110000.0;
                score -= sameTypeShapeNear * 240000.0;
            }
            else if (placementMode == PlacementModeSmallHug)
            {
                score -= sameTypeContact * 1200000.0;
                score -= sameTypeNear * 160000.0;
                score -= sameTypeShapeNear * 420000.0;
                score += addedX * 900000.0;
            }

            return score;
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
            if (best > maxDistance)
            {
                return 0.0;
            }
            return maxDistance - best;
        }

        private static double ContactLength(ShapeBounds a, ShapeBounds b, double tolerance)
        {
            double vertical = 0.0;
            if (Math.Abs(a.MaxX - b.MinX) <= tolerance || Math.Abs(b.MaxX - a.MinX) <= tolerance)
            {
                vertical = OverlapLength(a.MinY, a.MaxY, b.MinY, b.MaxY);
            }
            double horizontal = 0.0;
            if (Math.Abs(a.MaxY - b.MinY) <= tolerance || Math.Abs(b.MaxY - a.MinY) <= tolerance)
            {
                horizontal = OverlapLength(a.MinX, a.MaxX, b.MinX, b.MaxX);
            }
            return Math.Max(0.0, vertical) + Math.Max(0.0, horizontal);
        }

        private static double OverlapLength(double a1, double a2, double b1, double b2)
        {
            return Math.Min(a2, b2) - Math.Max(a1, b1);
        }

        private static double NarrowVoidPenalty(ShapeBounds a, ShapeBounds b)
        {
            double penalty = 0.0;
            double xGap = Math.Max(0.0, Math.Max(a.MinX, b.MinX) - Math.Min(a.MaxX, b.MaxX));
            double yOverlap = OverlapLength(a.MinY, a.MaxY, b.MinY, b.MaxY);
            if (xGap > 8.0 && xGap < 85.0 && yOverlap > 25.0)
            {
                penalty += (85.0 - xGap) * yOverlap;
            }
            double yGap = Math.Max(0.0, Math.Max(a.MinY, b.MinY) - Math.Min(a.MaxY, b.MaxY));
            double xOverlap = OverlapLength(a.MinX, a.MaxX, b.MinX, b.MaxX);
            if (yGap > 8.0 && yGap < 85.0 && xOverlap > 25.0)
            {
                penalty += (85.0 - yGap) * xOverlap;
            }
            return penalty;
        }

        private static double GapBetween(ShapeBounds a, ShapeBounds b)
        {
            double dx = 0.0;
            if (a.MaxX < b.MinX)
            {
                dx = b.MinX - a.MaxX;
            }
            else if (b.MaxX < a.MinX)
            {
                dx = a.MinX - b.MaxX;
            }
            double dy = 0.0;
            if (a.MaxY < b.MinY)
            {
                dy = b.MinY - a.MaxY;
            }
            else if (b.MaxY < a.MinY)
            {
                dy = a.MinY - b.MaxY;
            }
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static List<ShapePoint> CandidatePoints(List<ShapePlacement> placed, ShapePartInstance partInstance, ShapePolygon part, ShapeNestingInput input, int placementMode)
        {
            List<ShapePoint> points = new List<ShapePoint>();
            points.Add(new ShapePoint(0.0, 0.0));
            List<ShapePoint> anchors = AnchorPoints(part);
            foreach (ShapePlacement p in placed)
            {
                ShapeBounds b = p.Bounds;
                points.Add(new ShapePoint(b.MaxX, b.MinY));
                points.Add(new ShapePoint(b.MinX, b.MaxY));
                points.Add(new ShapePoint(b.MaxX, b.MaxY));
                points.Add(new ShapePoint(b.MaxX, 0.0));
                points.Add(new ShapePoint(0.0, b.MaxY));

                int step = Math.Max(1, p.PlacedPolygon.Points.Count / 8);
                for (int i = 0; i < p.PlacedPolygon.Points.Count; i += step)
                {
                    ShapePoint existing = p.PlacedPolygon.Points[i];
                    foreach (ShapePoint anchor in anchors)
                    {
                        double x = existing.X - anchor.X;
                        double y = existing.Y - anchor.Y;
                        if (x >= -1e-6 && y >= -1e-6 && x <= input.BoardWidth + 1e-6 && y <= input.BoardHeight + 1e-6)
                        {
                            points.Add(new ShapePoint(x, y));
                        }
                    }
                }
            }
            AddHugCandidatePoints(points, placed, partInstance, part, input, placementMode);
            AddGridCandidatePoints(points, placed, part, input);
            return SelectCandidatePoints(points);
        }

        private static void AddHugCandidatePoints(List<ShapePoint> points, List<ShapePlacement> placed, ShapePartInstance partInstance, ShapePolygon part, ShapeNestingInput input, int placementMode)
        {
            if (placed.Count == 0)
            {
                return;
            }
            if (placementMode != PlacementModeLargeHug && placementMode != PlacementModeSmallHug)
            {
                return;
            }
            int added = 0;
            List<ShapePoint> movingAnchors = HugAnchorPoints(part);
            foreach (ShapePlacement existing in placed)
            {
                if (existing.Part.TypeIndex != partInstance.TypeIndex && PartStage(existing.Part) != PartStage(partInstance))
                {
                    continue;
                }
                List<ShapePoint> existingAnchors = HugAnchorPoints(existing.PlacedPolygon);
                foreach (ShapePoint fixedPoint in existingAnchors)
                {
                    foreach (ShapePoint movingPoint in movingAnchors)
                    {
                        AddHugOffset(points, input, part, fixedPoint, movingPoint, HugGap, 0.0, ref added);
                        AddHugOffset(points, input, part, fixedPoint, movingPoint, -HugGap, 0.0, ref added);
                        AddHugOffset(points, input, part, fixedPoint, movingPoint, 0.0, HugGap, ref added);
                        AddHugOffset(points, input, part, fixedPoint, movingPoint, 0.0, -HugGap, ref added);
                        if (added >= HugCandidateLimit)
                        {
                            return;
                        }
                    }
                }
            }
        }

        private static void AddHugOffset(List<ShapePoint> points, ShapeNestingInput input, ShapePolygon part, ShapePoint fixedPoint, ShapePoint movingPoint, double dx, double dy, ref int added)
        {
            ShapePoint candidate = new ShapePoint(fixedPoint.X - movingPoint.X + dx, fixedPoint.Y - movingPoint.Y + dy);
            ShapeBounds b = part.Translate(candidate.X, candidate.Y).Bounds();
            if (b.MinX < -1e-6 || b.MinY < -1e-6 || b.MaxX > input.BoardWidth + 1e-6 || b.MaxY > input.BoardHeight + 1e-6)
            {
                return;
            }
            points.Add(candidate);
            added++;
        }

        private static void AddGridCandidatePoints(List<ShapePoint> points, List<ShapePlacement> placed, ShapePolygon part, ShapeNestingInput input)
        {
            if (placed.Count == 0)
            {
                return;
            }
            ShapeBounds partBounds = part.Bounds();
            List<double> xs = new List<double>();
            List<double> ys = new List<double>();
            xs.Add(0.0);
            ys.Add(0.0);
            foreach (ShapePlacement p in placed)
            {
                ShapeBounds b = p.Bounds;
                AddAxisValue(xs, b.MinX);
                AddAxisValue(xs, b.MaxX);
                AddAxisValue(xs, Math.Max(0.0, b.MinX - partBounds.Width));
                AddAxisValue(xs, Math.Max(0.0, b.MaxX - partBounds.Width));
                AddAxisValue(ys, b.MinY);
                AddAxisValue(ys, b.MaxY);
                AddAxisValue(ys, Math.Max(0.0, b.MinY - partBounds.Height));
                AddAxisValue(ys, Math.Max(0.0, b.MaxY - partBounds.Height));
            }
            xs.Sort();
            ys.Sort();
            TrimAxisValues(xs, GridXLimit);
            TrimAxisValues(ys, GridYLimit);
            foreach (double x in xs)
            {
                if (x < -1e-6 || x + partBounds.Width > input.BoardWidth + 1e-6)
                {
                    continue;
                }
                foreach (double y in ys)
                {
                    if (y < -1e-6 || y + partBounds.Height > input.BoardHeight + 1e-6)
                    {
                        continue;
                    }
                    points.Add(new ShapePoint(x, y));
                }
            }
        }

        private static void AddAxisValue(List<double> values, double value)
        {
            if (value < -1e-6)
            {
                return;
            }
            foreach (double existing in values)
            {
                if (Math.Abs(existing - value) < 1e-3)
                {
                    return;
                }
            }
            values.Add(value);
        }

        private static void TrimAxisValues(List<double> values, int max)
        {
            if (values.Count <= max)
            {
                return;
            }
            while (values.Count > max)
            {
                int removeIndex = 1;
                double bestGap = double.PositiveInfinity;
                for (int i = 1; i < values.Count - 1; i++)
                {
                    double gap = values[i + 1] - values[i - 1];
                    if (gap < bestGap)
                    {
                        bestGap = gap;
                        removeIndex = i;
                    }
                }
                values.RemoveAt(removeIndex);
            }
        }

        private static List<ShapePoint> SelectCandidatePoints(List<ShapePoint> points)
        {
            List<ShapePoint> uniqueAll = new List<ShapePoint>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            foreach (ShapePoint p in points)
            {
                string key = ((int)Math.Round(p.X * 1000.0)).ToString(CultureInfo.InvariantCulture) + "," +
                    ((int)Math.Round(p.Y * 1000.0)).ToString(CultureInfo.InvariantCulture);
                if (!seen.ContainsKey(key))
                {
                    seen.Add(key, true);
                    uniqueAll.Add(p);
                }
            }

            uniqueAll.Sort(delegate (ShapePoint a, ShapePoint b)
            {
                int cmp = a.X.CompareTo(b.X);
                return cmp != 0 ? cmp : a.Y.CompareTo(b.Y);
            });

            List<List<ShapePoint>> buckets = new List<List<ShapePoint>>();
            int i = 0;
            while (i < uniqueAll.Count)
            {
                double bucketX = uniqueAll[i].X;
                List<ShapePoint> bucket = new List<ShapePoint>();
                while (i < uniqueAll.Count && Math.Abs(uniqueAll[i].X - bucketX) < 25.0)
                {
                    bucket.Add(uniqueAll[i]);
                    i++;
                }
                bucket.Sort(delegate (ShapePoint a, ShapePoint b)
                {
                    int cmp = a.Y.CompareTo(b.Y);
                    return cmp != 0 ? cmp : a.X.CompareTo(b.X);
                });
                buckets.Add(bucket);
            }

            List<ShapePoint> selected = new List<ShapePoint>();
            Dictionary<string, bool> selectedSeen = new Dictionary<string, bool>();

            List<int> fullWidthOrder = CandidateBucketOrder(buckets.Count, FullWidthCandidateSlots);
            for (int depth = 0; depth < FullWidthCandidateDepth && selected.Count < MaxCandidates; depth++)
            {
                foreach (int bucketIndex in fullWidthOrder)
                {
                    if (selected.Count >= MaxCandidates)
                    {
                        break;
                    }
                    AddCandidateFromBucket(selected, selectedSeen, buckets, bucketIndex, depth);
                }
            }

            int leftBuckets = Math.Min(LeftCandidateBuckets, buckets.Count);
            for (int depth = 0; depth < LeftCandidateDepth && selected.Count < MaxCandidates; depth++)
            {
                for (int bucketIndex = 0; bucketIndex < leftBuckets && selected.Count < MaxCandidates; bucketIndex++)
                {
                    AddCandidateFromBucket(selected, selectedSeen, buckets, bucketIndex, depth);
                }
            }

            List<int> fillOrder = CandidateBucketOrder(buckets.Count, MaxCandidates);
            for (int depth = 0; selected.Count < MaxCandidates; depth++)
            {
                bool added = false;
                foreach (int bucketIndex in fillOrder)
                {
                    int before = selected.Count;
                    AddCandidateFromBucket(selected, selectedSeen, buckets, bucketIndex, depth);
                    if (selected.Count > before)
                    {
                        added = true;
                    }
                    if (selected.Count >= MaxCandidates)
                    {
                        break;
                    }
                }
                if (!added)
                {
                    break;
                }
            }

            selected.Sort(delegate (ShapePoint a, ShapePoint b)
            {
                int cmp = a.X.CompareTo(b.X);
                return cmp != 0 ? cmp : a.Y.CompareTo(b.Y);
            });
            return selected;
        }

        private static void AddCandidateFromBucket(List<ShapePoint> selected, Dictionary<string, bool> selectedSeen, List<List<ShapePoint>> buckets, int bucketIndex, int depth)
        {
            if (bucketIndex < 0 || bucketIndex >= buckets.Count)
            {
                return;
            }
            List<ShapePoint> bucket = buckets[bucketIndex];
            if (depth < 0 || depth >= bucket.Count)
            {
                return;
            }
            ShapePoint p = bucket[depth];
            string key = CandidateKey(p);
            if (selectedSeen.ContainsKey(key))
            {
                return;
            }
            selectedSeen.Add(key, true);
            selected.Add(p);
        }

        private static string CandidateKey(ShapePoint p)
        {
            return ((int)Math.Round(p.X * 1000.0)).ToString(CultureInfo.InvariantCulture) + "," +
                ((int)Math.Round(p.Y * 1000.0)).ToString(CultureInfo.InvariantCulture);
        }

        private static List<int> CandidateBucketOrder(int bucketCount, int maxSlots)
        {
            List<int> order = new List<int>();
            if (bucketCount <= 0)
            {
                return order;
            }
            if (maxSlots <= 0)
            {
                return order;
            }
            if (bucketCount <= maxSlots)
            {
                for (int i = 0; i < bucketCount; i++)
                {
                    order.Add(i);
                }
                return order;
            }

            bool[] used = new bool[bucketCount];
            for (int slot = 0; slot < maxSlots; slot++)
            {
                int index = maxSlots == 1 ? 0 : (int)Math.Round((bucketCount - 1) * slot / (double)(maxSlots - 1));
                if (!used[index])
                {
                    used[index] = true;
                    order.Add(index);
                }
            }
            for (int i = 0; order.Count < maxSlots && i < bucketCount; i++)
            {
                if (!used[i])
                {
                    used[i] = true;
                    order.Add(i);
                }
            }
            order.Sort();
            return order;
        }

        private static List<ShapePoint> AnchorPoints(ShapePolygon part)
        {
            ShapeBounds b = part.Bounds();
            List<ShapePoint> anchors = new List<ShapePoint>();
            anchors.Add(new ShapePoint(0.0, 0.0));
            anchors.Add(new ShapePoint(b.Width, 0.0));
            anchors.Add(new ShapePoint(0.0, b.Height));
            anchors.Add(new ShapePoint(b.Width, b.Height));

            int step = Math.Max(1, part.Points.Count / 8);
            for (int i = 0; i < part.Points.Count; i += step)
            {
                anchors.Add(part.Points[i]);
            }
            return anchors;
        }

        private static List<ShapePoint> HugAnchorPoints(ShapePolygon part)
        {
            List<ShapePoint> anchors = new List<ShapePoint>();
            ShapeBounds b = part.Bounds();
            anchors.Add(new ShapePoint(b.MinX, b.MinY));
            anchors.Add(new ShapePoint(b.MaxX, b.MinY));
            anchors.Add(new ShapePoint(b.MinX, b.MaxY));
            anchors.Add(new ShapePoint(b.MaxX, b.MaxY));
            anchors.Add(new ShapePoint((b.MinX + b.MaxX) / 2.0, b.MinY));
            anchors.Add(new ShapePoint((b.MinX + b.MaxX) / 2.0, b.MaxY));
            anchors.Add(new ShapePoint(b.MinX, (b.MinY + b.MaxY) / 2.0));
            anchors.Add(new ShapePoint(b.MaxX, (b.MinY + b.MaxY) / 2.0));
            int step = Math.Max(1, part.Points.Count / 10);
            for (int i = 0; i < part.Points.Count; i += step)
            {
                anchors.Add(part.Points[i]);
            }
            return anchors;
        }

        private static double MaxUsedX(ShapeNestingResult result)
        {
            double max = 0.0;
            foreach (ShapePlacement p in result.Placements)
            {
                max = Math.Max(max, p.Bounds.MaxX);
            }
            return max;
        }

        private static double SumUsedXByBoard(ShapeNestingResult result)
        {
            double[] used = new double[Math.Max(result.BoardCount, 1)];
            foreach (ShapePlacement p in result.Placements)
            {
                ShapeBounds b = p.Bounds;
                if (p.BoardIndex >= 0 && p.BoardIndex < used.Length)
                {
                    used[p.BoardIndex] = Math.Max(used[p.BoardIndex], b.MaxX);
                }
            }
            double sum = 0.0;
            for (int i = 0; i < used.Length; i++)
            {
                sum += used[i];
            }
            return sum;
        }

        private static double SumUsedYByBoard(ShapeNestingResult result)
        {
            double[] used = new double[Math.Max(result.BoardCount, 1)];
            foreach (ShapePlacement p in result.Placements)
            {
                ShapeBounds b = p.Bounds;
                if (p.BoardIndex >= 0 && p.BoardIndex < used.Length)
                {
                    used[p.BoardIndex] = Math.Max(used[p.BoardIndex], b.MaxY);
                }
            }
            double sum = 0.0;
            for (int i = 0; i < used.Length; i++)
            {
                sum += used[i];
            }
            return sum;
        }

        private static double SumEnvelopeAreaByBoard(ShapeNestingResult result)
        {
            double[] usedX = new double[Math.Max(result.BoardCount, 1)];
            double[] usedY = new double[Math.Max(result.BoardCount, 1)];
            foreach (ShapePlacement p in result.Placements)
            {
                ShapeBounds b = p.Bounds;
                if (p.BoardIndex >= 0 && p.BoardIndex < usedX.Length)
                {
                    usedX[p.BoardIndex] = Math.Max(usedX[p.BoardIndex], b.MaxX);
                    usedY[p.BoardIndex] = Math.Max(usedY[p.BoardIndex], b.MaxY);
                }
            }
            double sum = 0.0;
            for (int i = 0; i < usedX.Length; i++)
            {
                sum += usedX[i] * usedY[i];
            }
            return sum;
        }

        private static double TotalRightRemnant(ShapeNestingResult result)
        {
            return result.Input.BoardWidth * Math.Max(result.BoardCount, 1) - SumUsedXByBoard(result);
        }

        private static double MaxUsedY(ShapeNestingResult result)
        {
            double max = 0.0;
            foreach (ShapePlacement p in result.Placements)
            {
                max = Math.Max(max, p.Bounds.MaxY);
            }
            return max;
        }

        private static ShapeChromosome BestEvaluated(List<ShapeChromosome> population)
        {
            foreach (ShapeChromosome c in population)
            {
                if (c.Result != null)
                {
                    return c.Clone();
                }
            }
            return null;
        }

        private static int CountEvaluated(List<ShapeChromosome> population)
        {
            int count = 0;
            foreach (ShapeChromosome c in population)
            {
                if (c.Result != null)
                {
                    count++;
                }
            }
            return count;
        }

        private static ShapeChromosome Tournament(List<ShapeChromosome> population, Random random)
        {
            ShapeChromosome best = null;
            for (int i = 0; i < 3; i++)
            {
                ShapeChromosome c = population[random.Next(population.Count)];
                if (best == null || Compare(c, best) < 0)
                {
                    best = c;
                }
            }
            return best;
        }

        private static ShapeChromosome Crossover(ShapeChromosome a, ShapeChromosome b, Random random, List<ShapePartInstance> parts)
        {
            int n = a.Order.Length;
            ShapeChromosome child = NewChromosome(n);
            for (int i = 0; i < n; i++)
            {
                child.Order[i] = -1;
            }
            int cut1 = random.Next(n);
            int cut2 = random.Next(n);
            if (cut1 > cut2)
            {
                int tmp = cut1;
                cut1 = cut2;
                cut2 = tmp;
            }
            bool[] used = new bool[n];
            for (int i = cut1; i <= cut2; i++)
            {
                child.Order[i] = a.Order[i];
                used[child.Order[i]] = true;
            }
            int write = (cut2 + 1) % n;
            for (int i = 0; i < n; i++)
            {
                int gene = b.Order[(cut2 + 1 + i) % n];
                if (!used[gene])
                {
                    child.Order[write] = gene;
                    used[gene] = true;
                    write = (write + 1) % n;
                }
            }
            for (int i = 0; i < n; i++)
            {
                child.RotationGenes[i] = random.NextDouble() < 0.5 ? a.RotationGenes[i] : b.RotationGenes[i];
            }
            EnforceStageOrder(child.Order, parts);
            return child;
        }

        private static void Mutate(ShapeChromosome c, Random random, List<ShapePartInstance> parts)
        {
            int n = c.Order.Length;
            if (random.NextDouble() < 0.8)
            {
                int a = random.Next(n);
                int radius = 8 + random.Next(24);
                int b = Math.Max(0, Math.Min(n - 1, a + random.Next(radius * 2 + 1) - radius));
                int tmp = c.Order[a];
                c.Order[a] = c.Order[b];
                c.Order[b] = tmp;
            }
            if (random.NextDouble() < 0.45)
            {
                int start = random.Next(n);
                int length = Math.Min(n - start, 4 + random.Next(12));
                Array.Reverse(c.Order, start, length);
            }
            if (random.NextDouble() < 0.3)
            {
                int swaps = Math.Max(2, n / 50);
                for (int s = 0; s < swaps; s++)
                {
                    int a = random.Next(n);
                    int b = random.Next(n);
                    int tmp = c.Order[a];
                    c.Order[a] = c.Order[b];
                    c.Order[b] = tmp;
                }
            }
            for (int i = 0; i < n; i++)
            {
                if (random.NextDouble() < 0.09)
                {
                    c.RotationGenes[i] = random.Next(Rotations.Length);
                }
            }
            EnforceStageOrder(c.Order, parts);
        }

        private static int Compare(ShapeChromosome a, ShapeChromosome b)
        {
            int resultCompare = CompareResults(a == null ? null : a.Result, b == null ? null : b.Result);
            return resultCompare != 0 ? resultCompare : a.Fitness.CompareTo(b.Fitness);
        }

        private static double SeedFitness(ShapeNestingResult result)
        {
            if (result == null)
            {
                return double.PositiveInfinity;
            }
            return result.BoardCount * 1000000000000.0 -
                result.TotalRightRemnant * 10000000.0 +
                MaxUsedX(result) * 100000.0 +
                SumUsedYByBoard(result) * 60000.0 +
                SumEnvelopeAreaByBoard(result) * 2.0;
        }

        private static int CompareResults(ShapeNestingResult a, ShapeNestingResult b)
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

            int cmp = CompareDescending(a.TotalRightRemnant, b.TotalRightRemnant, 1e-6);
            if (cmp != 0)
            {
                return cmp;
            }
            cmp = CompareDescending(a.MinRightRemnant, b.MinRightRemnant, 1e-6);
            if (cmp != 0)
            {
                return cmp;
            }
            cmp = CompareAscending(SumEnvelopeAreaByBoard(a), SumEnvelopeAreaByBoard(b), 1e-6);
            if (cmp != 0)
            {
                return cmp;
            }
            cmp = CompareAscending(SumUsedYByBoard(a), SumUsedYByBoard(b), 1e-6);
            if (cmp != 0)
            {
                return cmp;
            }
            return CompareAscending(MaxUsedX(a), MaxUsedX(b), 1e-6);
        }

        private static bool IsVisibleImprovement(ShapeNestingResult candidate, ShapeNestingResult current)
        {
            if (current == null)
            {
                return candidate != null;
            }
            if (candidate == null)
            {
                return false;
            }
            if (candidate.BoardCount < current.BoardCount)
            {
                return true;
            }
            if (candidate.BoardCount > current.BoardCount)
            {
                return false;
            }
            int cmp = CompareDescending(candidate.TotalRightRemnant, current.TotalRightRemnant, 1e-3);
            if (cmp < 0)
            {
                return true;
            }
            if (cmp > 0)
            {
                return false;
            }
            return CompareDescending(candidate.MinRightRemnant, current.MinRightRemnant, 1e-3) < 0;
        }

        private static int CompareDescending(double a, double b, double tolerance)
        {
            double delta = a - b;
            if (Math.Abs(delta) <= tolerance)
            {
                return 0;
            }
            return delta > 0.0 ? -1 : 1;
        }

        private static int CompareAscending(double a, double b, double tolerance)
        {
            double delta = a - b;
            if (Math.Abs(delta) <= tolerance)
            {
                return 0;
            }
            return delta < 0.0 ? -1 : 1;
        }

        private static void Shuffle(int[] values, Random random)
        {
            for (int i = values.Length - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                int tmp = values[i];
                values[i] = values[j];
                values[j] = tmp;
            }
        }

        private static void ShuffleWithinStages(int[] order, List<ShapePartInstance> parts, Random random)
        {
            for (int stage = 0; stage <= 2; stage++)
            {
                List<int> indexes = new List<int>();
                for (int i = 0; i < order.Length; i++)
                {
                    if (PartStage(parts[order[i]]) == stage)
                    {
                        indexes.Add(i);
                    }
                }
                for (int i = indexes.Count - 1; i > 0; i--)
                {
                    int j = random.Next(i + 1);
                    int ia = indexes[i];
                    int ib = indexes[j];
                    int tmp = order[ia];
                    order[ia] = order[ib];
                    order[ib] = tmp;
                }
            }
        }

        private static void EnforceStageOrder(int[] order, List<ShapePartInstance> parts)
        {
            List<int> sorted = new List<int>(order.Length);
            for (int stage = 0; stage <= 2; stage++)
            {
                for (int i = 0; i < order.Length; i++)
                {
                    if (PartStage(parts[order[i]]) == stage)
                    {
                        sorted.Add(order[i]);
                    }
                }
            }
            for (int i = 0; i < order.Length; i++)
            {
                order[i] = sorted[i];
            }
        }
    }

    internal sealed class ShapeChromosome
    {
        public int[] Order;
        public int[] RotationGenes;
        public ShapeNestingResult Result;
        public double Fitness;
        public int Generation;
        public int Individual;

        public ShapeChromosome Clone()
        {
            ShapeChromosome c = new ShapeChromosome();
            c.Order = (int[])Order.Clone();
            c.RotationGenes = (int[])RotationGenes.Clone();
            c.Result = Result;
            c.Fitness = Fitness;
            c.Generation = Generation;
            c.Individual = Individual;
            return c;
        }
    }

    internal sealed class EvalStats
    {
        public int Completed;
        public int Skipped;
        public int Rejected;
    }

    internal static class ShapeCollision
    {
        private const double CollisionTolerance = 0.2;
        private const double OrientationTolerance = 1e-7;

        public static bool Intersects(ShapePolygon a, ShapePolygon b)
        {
            return Intersects(a, a.Bounds(), b, b.Bounds());
        }

        public static bool Intersects(ShapePolygon a, ShapeBounds ab, ShapePolygon b, ShapeBounds bb)
        {
            if (!BoundsOverlap(ab, bb))
            {
                return false;
            }
            if (NearlySameBounds(ab, bb) && Math.Abs(a.Area() - b.Area()) < 1e-4)
            {
                return true;
            }
            for (int i = 0; i < a.Points.Count; i++)
            {
                ShapePoint a1 = a.Points[i];
                ShapePoint a2 = a.Points[(i + 1) % a.Points.Count];
                for (int j = 0; j < b.Points.Count; j++)
                {
                    ShapePoint b1 = b.Points[j];
                    ShapePoint b2 = b.Points[(j + 1) % b.Points.Count];
                    if (SegmentsCross(a1, a2, b1, b2))
                    {
                        return true;
                    }
                }
            }
            foreach (ShapePoint p in a.Points)
            {
                if (PointInPolygon(p, b))
                {
                    return true;
                }
            }
            foreach (ShapePoint p in b.Points)
            {
                if (PointInPolygon(p, a))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool NearlySameBounds(ShapeBounds a, ShapeBounds b)
        {
            return Math.Abs(a.MinX - b.MinX) < 1e-5 &&
                Math.Abs(a.MinY - b.MinY) < 1e-5 &&
                Math.Abs(a.MaxX - b.MaxX) < 1e-5 &&
                Math.Abs(a.MaxY - b.MaxY) < 1e-5;
        }

        public static bool BoundsOverlap(ShapeBounds ab, ShapeBounds bb)
        {
            return !(ab.MaxX < bb.MinX - CollisionTolerance || bb.MaxX < ab.MinX - CollisionTolerance ||
                ab.MaxY < bb.MinY - CollisionTolerance || bb.MaxY < ab.MinY - CollisionTolerance);
        }

        private static bool SegmentsCross(ShapePoint a, ShapePoint b, ShapePoint c, ShapePoint d)
        {
            double o1 = Orient(a, b, c);
            double o2 = Orient(a, b, d);
            double o3 = Orient(c, d, a);
            double o4 = Orient(c, d, b);
            if (OppositeSigns(o1, o2) && OppositeSigns(o3, o4))
            {
                return true;
            }
            if (Math.Abs(o1) <= OrientationTolerance && OnSegment(a, b, c)) return true;
            if (Math.Abs(o2) <= OrientationTolerance && OnSegment(a, b, d)) return true;
            if (Math.Abs(o3) <= OrientationTolerance && OnSegment(c, d, a)) return true;
            if (Math.Abs(o4) <= OrientationTolerance && OnSegment(c, d, b)) return true;
            return SegmentDistance(a, b, c, d) <= CollisionTolerance;
        }

        private static bool OppositeSigns(double a, double b)
        {
            return (a > OrientationTolerance && b < -OrientationTolerance) ||
                (a < -OrientationTolerance && b > OrientationTolerance);
        }

        private static double SegmentDistance(ShapePoint a, ShapePoint b, ShapePoint c, ShapePoint d)
        {
            return Math.Min(
                Math.Min(PointSegmentDistance(a, c, d), PointSegmentDistance(b, c, d)),
                Math.Min(PointSegmentDistance(c, a, b), PointSegmentDistance(d, a, b)));
        }

        private static double PointSegmentDistance(ShapePoint p, ShapePoint a, ShapePoint b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double len2 = dx * dx + dy * dy;
            if (len2 < 1e-12)
            {
                double px = p.X - a.X;
                double py = p.Y - a.Y;
                return Math.Sqrt(px * px + py * py);
            }
            double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
            t = Math.Max(0.0, Math.Min(1.0, t));
            double x = a.X + t * dx;
            double y = a.Y + t * dy;
            double ddx = p.X - x;
            double ddy = p.Y - y;
            return Math.Sqrt(ddx * ddx + ddy * ddy);
        }

        private static double CollinearOverlapLength(ShapePoint a, ShapePoint b, ShapePoint c, ShapePoint d)
        {
            double ax = Math.Abs(a.X - b.X);
            double ay = Math.Abs(a.Y - b.Y);
            if (ax >= ay)
            {
                double min1 = Math.Min(a.X, b.X);
                double max1 = Math.Max(a.X, b.X);
                double min2 = Math.Min(c.X, d.X);
                double max2 = Math.Max(c.X, d.X);
                return Math.Min(max1, max2) - Math.Max(min1, min2);
            }
            else
            {
                double min1 = Math.Min(a.Y, b.Y);
                double max1 = Math.Max(a.Y, b.Y);
                double min2 = Math.Min(c.Y, d.Y);
                double max2 = Math.Max(c.Y, d.Y);
                return Math.Min(max1, max2) - Math.Max(min1, min2);
            }
        }

        private static bool SamePoint(ShapePoint a, ShapePoint b)
        {
            return Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6;
        }

        private static double Orient(ShapePoint a, ShapePoint b, ShapePoint c)
        {
            return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        }

        private static bool OnSegment(ShapePoint a, ShapePoint b, ShapePoint p)
        {
            return p.X >= Math.Min(a.X, b.X) - CollisionTolerance && p.X <= Math.Max(a.X, b.X) + CollisionTolerance &&
                p.Y >= Math.Min(a.Y, b.Y) - CollisionTolerance && p.Y <= Math.Max(a.Y, b.Y) + CollisionTolerance;
        }

        private static bool PointInPolygon(ShapePoint p, ShapePolygon poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Points.Count - 1; i < poly.Points.Count; j = i++)
            {
                ShapePoint pi = poly.Points[i];
                ShapePoint pj = poly.Points[j];
                if (Math.Abs(Orient(pj, pi, p)) < 1e-7 && OnSegment(pj, pi, p))
                {
                    return false;
                }
                if (((pi.Y > p.Y) != (pj.Y > p.Y)) &&
                    (p.X < (pj.X - pi.X) * (p.Y - pi.Y) / (pj.Y - pi.Y + 1e-12) + pi.X))
                {
                    inside = !inside;
                }
            }
            return inside;
        }
    }

    internal static class ShapeNestingValidator
    {
        public static void Validate(ShapeNestingResult result)
        {
            if (result.Utilization > 1.0001)
            {
                throw new InvalidOperationException("Invalid nesting result: utilization is greater than 100%.");
            }
            List<ShapePlacement> expanded = ExpandedPlacements(result.Placements);
            for (int i = 0; i < expanded.Count; i++)
            {
                ShapePlacement a = expanded[i];
                for (int j = i + 1; j < expanded.Count; j++)
                {
                    ShapePlacement b = expanded[j];
                    if (a.BoardIndex != b.BoardIndex)
                    {
                        continue;
                    }
                    if (ShapeCollision.BoundsOverlap(a.Bounds, b.Bounds) &&
                        ShapeCollision.Intersects(a.PlacedPolygon, a.Bounds, b.PlacedPolygon, b.Bounds))
                    {
                        throw new InvalidOperationException("Invalid nesting result: overlapping parts on sheet " + (a.BoardIndex + 1).ToString(CultureInfo.InvariantCulture) + ".");
                    }
                }
            }
        }

        public static List<ShapePlacement> ExpandedPlacements(List<ShapePlacement> placements)
        {
            List<ShapePlacement> expanded = new List<ShapePlacement>();
            foreach (ShapePlacement placement in placements)
            {
                if (placement.Part.Components == null || placement.Part.Components.Count == 0)
                {
                    expanded.Add(placement);
                    continue;
                }
                foreach (ShapePartComponent component in placement.Part.Components)
                {
                    ShapePolygon polygon = component.LocalPolygon.Translate(placement.X, placement.Y);
                    expanded.Add(new ShapePlacement
                    {
                        Part = component.Part,
                        BoardIndex = placement.BoardIndex,
                        X = placement.X,
                        Y = placement.Y,
                        Rotation = placement.Rotation,
                        PlacedPolygon = polygon,
                        Bounds = polygon.Bounds()
                    });
                }
            }
            return expanded;
        }
    }

    internal static class ShapeNestingDrawer
    {
        private const string ResultRegApp = "CDX_SHAPE_NEST_RESULT";
        private const string ResultBoardLayer = "CDX_NEST_RESULT_BOARD";
        private const string ResultPartLayer = "CDX_NEST_RESULT_PART";
        private const string ResultTextLayer = "CDX_NEST_RESULT_TEXT";

        public static void Draw(Document doc, ShapeNestingResult result, Point3d origin)
        {
            Redraw(doc, result, origin);
        }

        public static void Redraw(Document doc, ShapeNestingResult result, Point3d origin)
        {
            Database db = doc.Database;
            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                EnsureRegApp(db, tr, ResultRegApp);
                EnsureLayer(db, tr, ResultBoardLayer, 7);
                EnsureLayer(db, tr, ResultPartLayer, 3);
                EnsureLayer(db, tr, ResultTextLayer, 2);

                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                ErasePreviousResult(ms, tr);
                double gap = 350.0;

                for (int b = 0; b < result.BoardCount; b++)
                {
                    double ox = origin.X;
                    double oy = origin.Y - b * (result.Input.BoardHeight + gap);
                    AppendRectangle(ms, tr, ox, oy, result.Input.BoardWidth, result.Input.BoardHeight, ResultBoardLayer, 7);
                    AppendText(ms, tr, new Point3d(ox, oy + result.Input.BoardHeight + 80, 0), 120, "Sheet " + (b + 1).ToString(CultureInfo.InvariantCulture), ResultTextLayer, 2);
                }

                foreach (ShapePlacement p in ShapeNestingValidator.ExpandedPlacements(result.Placements))
                {
                    double ox = origin.X;
                    double oy = origin.Y - p.BoardIndex * (result.Input.BoardHeight + gap);
                    List<ShapePoint> moved = new List<ShapePoint>();
                    foreach (ShapePoint sp in p.PlacedPolygon.Points)
                    {
                        moved.Add(new ShapePoint(sp.X + ox, sp.Y + oy));
                    }
                    AppendPolygon(ms, tr, new ShapePolygon(moved), ResultPartLayer, p.Part.ColorIndex);
                    ShapeBounds b = new ShapePolygon(moved).Bounds();
                    AppendText(ms, tr, new Point3d((b.MinX + b.MaxX) / 2.0, (b.MinY + b.MaxY) / 2.0, 0), 60, p.Part.Name + "-" + p.Part.Number.ToString(CultureInfo.InvariantCulture), ResultTextLayer, p.Part.ColorIndex);
                }

                string source = result.Generation >= 0 && result.Individual > 0
                    ? " gen " + result.Generation.ToString(CultureInfo.InvariantCulture) + " individual " + result.Individual.ToString(CultureInfo.InvariantCulture)
                    : string.Empty;
                string summary = "Shape nesting " + CodexBridgeVersion.Version + source + " sheets: " + result.BoardCount.ToString(CultureInfo.InvariantCulture) +
                    "  used board length: " + (result.Utilization * 100.0).ToString("0.00", CultureInfo.InvariantCulture) + "%" +
                    "  last right remnant: " + result.MinRightRemnant.ToString("0.###", CultureInfo.InvariantCulture) +
                    "  total right: " + result.TotalRightRemnant.ToString("0.###", CultureInfo.InvariantCulture) +
                    "  board: " + result.Input.BoardWidth.ToString("0.###", CultureInfo.InvariantCulture) +
                    " x " + result.Input.BoardHeight.ToString("0.###", CultureInfo.InvariantCulture);
                AppendText(ms, tr, new Point3d(origin.X, origin.Y + result.Input.BoardHeight + 260, 0), 140, summary, ResultTextLayer, 2);
                tr.Commit();
            }
            doc.Editor.Regen();
        }

        public static int Clear(Document doc)
        {
            if (doc == null)
            {
                return 0;
            }
            int count;
            Database db = doc.Database;
            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                count = ErasePreviousResult(ms, tr);
                tr.Commit();
            }
            doc.Editor.Regen();
            return count;
        }

        private static void AppendRectangle(BlockTableRecord ms, Transaction tr, double x, double y, double width, double height, string layer, short color)
        {
            List<ShapePoint> pts = new List<ShapePoint>();
            pts.Add(new ShapePoint(x, y));
            pts.Add(new ShapePoint(x + width, y));
            pts.Add(new ShapePoint(x + width, y + height));
            pts.Add(new ShapePoint(x, y + height));
            AppendPolygon(ms, tr, new ShapePolygon(pts), layer, color);
        }

        private static void AppendPolygon(BlockTableRecord ms, Transaction tr, ShapePolygon poly, string layer, short color)
        {
            Polyline pl = new Polyline(poly.Points.Count);
            for (int i = 0; i < poly.Points.Count; i++)
            {
                pl.AddVertexAt(i, new Point2d(poly.Points[i].X, poly.Points[i].Y), 0.0, 0.0, 0.0);
            }
            pl.Closed = true;
            pl.Layer = layer;
            pl.ColorIndex = color;
            MarkResultEntity(pl);
            ms.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);
        }

        private static void AppendText(BlockTableRecord ms, Transaction tr, Point3d point, double height, string value, string layer, short color)
        {
            DBText text = new DBText();
            text.Position = point;
            text.Height = height;
            text.TextString = value;
            text.Layer = layer;
            text.ColorIndex = color;
            MarkResultEntity(text);
            ms.AppendEntity(text);
            tr.AddNewlyCreatedDBObject(text, true);
        }

        private static int ErasePreviousResult(BlockTableRecord ms, Transaction tr)
        {
            List<ObjectId> erase = new List<ObjectId>();
            foreach (ObjectId id in ms)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                if (ent == null || !IsPreviousResultEntity(ent))
                {
                    continue;
                }
                erase.Add(id);
            }

            foreach (ObjectId id in erase)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                if (ent != null)
                {
                    ent.Erase();
                }
            }
            return erase.Count;
        }

        private static bool IsPreviousResultEntity(Entity ent)
        {
            if (HasResultTag(ent))
            {
                return true;
            }
            string layer = ent.Layer;
            return IsResultLayer(layer);
        }

        private static bool IsResultLayer(string layer)
        {
            return string.Equals(layer, ResultBoardLayer, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layer, ResultPartLayer, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layer, ResultTextLayer, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layer, "SHAPE_NEST_BOARD", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layer, "SHAPE_NEST_PART", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layer, "SHAPE_NEST_TEXT", StringComparison.OrdinalIgnoreCase);
        }

        private static double TotalRightRemnant(ShapeNestingResult result)
        {
            double[] used = new double[Math.Max(result.BoardCount, 1)];
            foreach (ShapePlacement p in result.Placements)
            {
                if (p.BoardIndex >= 0 && p.BoardIndex < used.Length)
                {
                    used[p.BoardIndex] = Math.Max(used[p.BoardIndex], p.Bounds.MaxX);
                }
            }
            double usedSum = 0.0;
            for (int i = 0; i < used.Length; i++)
            {
                usedSum += used[i];
            }
            return result.Input.BoardWidth * Math.Max(result.BoardCount, 1) - usedSum;
        }

        private static void MarkResultEntity(Entity ent)
        {
            ent.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, ResultRegApp),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "1"));
        }

        private static bool HasResultTag(Entity ent)
        {
            ResultBuffer rb = ent.GetXDataForApplication(ResultRegApp);
            if (rb == null)
            {
                return false;
            }
            rb.Dispose();
            return true;
        }

        private static void EnsureLayer(Database db, Transaction tr, string name, short colorIndex)
        {
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name))
            {
                return;
            }
            lt.UpgradeOpen();
            LayerTableRecord record = new LayerTableRecord();
            record.Name = name;
            record.Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex);
            lt.Add(record);
            tr.AddNewlyCreatedDBObject(record, true);
        }

        private static void EnsureRegApp(Database db, Transaction tr, string name)
        {
            RegAppTable table = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
            if (table.Has(name))
            {
                return;
            }
            table.UpgradeOpen();
            RegAppTableRecord record = new RegAppTableRecord();
            record.Name = name;
            table.Add(record);
            tr.AddNewlyCreatedDBObject(record, true);
        }
    }
}
