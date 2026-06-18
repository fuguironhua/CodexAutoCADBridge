using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.IO;
using ClipperLib;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using WinForms = System.Windows.Forms;

namespace CodexAutoCADBridge
{
    internal static class ShapeNestingV4Workflow
    {
        private static readonly object ActiveRunLock = new object();
        private static V4AsyncRun ActiveRun;

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

                PromptPointOptions pointOptions = new PromptPointOptions("\nSpecify drawing origin for V4 layered fill result");
                PromptPointResult pointResult = editor.GetPoint(pointOptions);
                if (pointResult.Status != PromptStatus.OK)
                {
                    return;
                }

                StartBackgroundRun(doc, input, pointResult.Value);
            }
            catch (System.Exception ex)
            {
                if (ex is OperationCanceledException)
                {
                    editor.WriteMessage("\nCDX_NEST_GROUP_V4 canceled.");
                    return;
                }
                editor.WriteMessage("\nCDX_NEST_GROUP_V4 failed: " + ex.Message);
            }
        }

        private static void StartBackgroundRun(Document doc, ShapeNestingInput input, Point3d origin)
        {
            Editor editor = doc.Editor;
            V4AsyncRun run = new V4AsyncRun(doc, input, origin);
            run.Logger = V4RunLogger.Start(doc, input);
            lock (ActiveRunLock)
            {
                if (ActiveRun != null && !ActiveRun.IsFinished)
                {
                    editor.WriteMessage("\nCDXNG4 is already calculating. Press Esc to cancel the running calculation.");
                    run.Logger.Dispose();
                    return;
                }
                ActiveRun = run;
            }

            try
            {
                run.Invoker = new WinForms.Control();
                run.Invoker.CreateControl();
                IntPtr handle = run.Invoker.Handle;
                run.DrainCallback = delegate
                {
                    DrainBackgroundRun(run);
                };
                run.IdleHandler = delegate
                {
                    DrainBackgroundRun(run);
                };
                WinForms.Application.Idle += run.IdleHandler;
            }
            catch
            {
                lock (ActiveRunLock)
                {
                    if (ActiveRun == run)
                    {
                        ActiveRun = null;
                    }
                }
                run.Logger.Dispose();
                if (run.Invoker != null)
                {
                    run.Invoker.Dispose();
                    run.Invoker = null;
                }
                throw;
            }

            run.Timer = new WinForms.Timer();
            run.Timer.Interval = 250;
            run.Timer.Tick += delegate
            {
                DrainBackgroundRun(run);
            };
            run.Timer.Start();
            DrainBackgroundRun(run);

            editor.WriteMessage("\nCalculating V4 layered fill nesting in background. You can keep panning/zooming; press Esc to cancel.");
            editor.WriteMessage("\nCDXNG4 log: " + run.Logger.FilePath);
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    ShapeNestingResult result = ShapeNestingV4Solver.Solve(input, delegate (string message)
                    {
                        run.Logger.Write(message);
                        run.EnqueueProgress(message);
                    }, null, null, delegate
                    {
                        return run.CancelRequested;
                    });
                    run.Logger.Write("\nCDXNG4 completed: " + ShapeNestingV4Solver.Format(result) + ".");
                    run.SetResult(result);
                }
                catch (System.Exception ex)
                {
                    run.Logger.Write("\nCDXNG4 error: " + ex);
                    run.SetError(ex);
                }
            });
        }

        private static void DrainBackgroundRun(V4AsyncRun run)
        {
            if (run == null)
            {
                return;
            }
            lock (ActiveRunLock)
            {
                if (ActiveRun != run)
                {
                    return;
                }
            }

            Editor editor = run.Document.Editor;
            if (!run.IsFinished && IsEscapePressed() && run.RequestCancel())
            {
                editor.WriteMessage("\nCDXNG4 cancel requested, stopping calculation...");
            }

            List<string> messages = run.TakeProgress();
            foreach (string message in messages)
            {
                editor.WriteMessage(message);
            }

            V4Preview preview = run.TakePreview();
            if (preview != null)
            {
                Stopwatch drawWatch = Stopwatch.StartNew();
                ShapeNestingV4Drawer.RedrawPreview(run.Document, preview, run.Origin, run.Input);
                editor.Regen();
                drawWatch.Stop();
                run.Logger.Write("\nDRAW preview " + drawWatch.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + "s");
            }

            List<ShapeNestingResult> candidates = run.TakeCandidates();
            foreach (ShapeNestingResult candidate in candidates)
            {
                try
                {
                    Stopwatch drawWatch = Stopwatch.StartNew();
                    ShapeNestingValidator.Validate(candidate);
                    ShapeNestingV4Drawer.DrawIndividualSnapshot(run.Document, candidate, run.Origin);
                    editor.Regen();
                    drawWatch.Stop();
                    run.Logger.Write("\nDRAW individual gen " + candidate.Generation.ToString(CultureInfo.InvariantCulture) + " individual " + candidate.Individual.ToString(CultureInfo.InvariantCulture) + " " + drawWatch.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + "s");
                }
                catch (System.Exception ex)
                {
                    editor.WriteMessage("\nV4 preview draw skipped: " + ex.Message);
                }
            }

            Autodesk.AutoCAD.ApplicationServices.Application.UpdateScreen();

            if (!run.IsFinished)
            {
                return;
            }

            if (run.Timer != null)
            {
                run.Timer.Stop();
                run.Timer.Dispose();
                run.Timer = null;
            }
            if (run.IdleHandler != null)
            {
                WinForms.Application.Idle -= run.IdleHandler;
                run.IdleHandler = null;
            }
            if (run.Invoker != null)
            {
                run.Invoker.Dispose();
                run.Invoker = null;
            }

            System.Exception error = run.Error;
            ShapeNestingResult result = run.Result;
            if (error != null)
            {
                if (error is OperationCanceledException)
                {
                    editor.WriteMessage("\nCDX_NEST_GROUP_V4 canceled.");
                    run.Logger.Write("\nCDX_NEST_GROUP_V4 canceled.");
                }
                else
                {
                    editor.WriteMessage("\nCDX_NEST_GROUP_V4 failed: " + error.Message);
                    run.Logger.Write("\nCDX_NEST_GROUP_V4 failed: " + error);
                }
            }
            else if (result != null)
            {
                ShapeNestingValidator.Validate(result);
                editor.WriteMessage("\nV4 layered fill result: " + ShapeNestingV4Solver.Format(result) + ".");
                Stopwatch drawWatch = Stopwatch.StartNew();
                ShapeNestingV4Drawer.DrawFinalSnapshot(run.Document, result, run.Origin);
                editor.Regen();
                drawWatch.Stop();
                run.Logger.Write("\nDRAW final " + drawWatch.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + "s");
                editor.WriteMessage("\nCDXNG4 completed: " + ShapeNestingV4Solver.Format(result) + ".");
            }
            editor.WriteMessage("\nCDXNG4 log saved: " + run.Logger.FilePath);
            run.Logger.Dispose();

            lock (ActiveRunLock)
            {
                if (ActiveRun == run)
                {
                    ActiveRun = null;
                }
            }
        }

        private static bool IsEscapePressed()
        {
            return (GetAsyncKeyState(0x1B) & 0x8000) != 0;
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);

        private static V4Preview MakeInitialPreview(ShapeNestingInput input)
        {
            if (input == null || input.PartTypes.Count == 0)
            {
                return null;
            }
            ShapePolygon polygon = input.PartTypes[0].Polygon.Normalize();
            V4Template template = new V4Template
            {
                Count = 1,
                Polygons = new List<ShapePolygon> { polygon },
                Bounds = polygon.Bounds()
            };
            return new V4Preview
            {
                Reason = "background starting",
                TypeIndex = 0,
                Template = template,
                Templates = new List<V4Template> { template },
                TypeIndices = new List<int> { 0 },
                Input = input
            };
        }
    }

    internal static class ShapeNestingV4Solver
    {
        private const double Gap = 2.0;
        private const int Generations = 10;
        private static readonly bool SelectiveSlidePolishExperiment = true;
        private static readonly bool FinalBoardSettleAfterSearch = false;
        private const int CandidateLimit = 96;
        private const int FillCandidateLimit = 64;
        private const int CoarseCandidateLimit = 36;
        private const int CoarseFillCandidateLimit = 24;
        private const int TemplateCandidateLimit = 48;
        private const int GroupCandidateLimit = 48;
        private const int PairFastCandidateLimit = 32;
        private const int NfpCandidateLimit = 56;
        private const int NfpBoardPlacementLimit = 10;
        private const int NfpPolygonPointLimit = 48;
        private const double NfpScale = 1000.0;
        private const double HugNearDistance = 55.0;
        private const int MaxPatternRepeat = 6;
        private const int FillPassLimit = 3;
        private const int CurrentTypeFillPassLimit = 1;
        private const int FillTypeLimit = 2;
        private const int HoleCandidateLimit = 18;
        private const int ExistingBoardTryLimit = 3;
        private const int MaxHugUnitBlockCount = 3;
        private const int MaxComboBlockSize = MaxHugUnitBlockCount * 2;
        private const int BatchCandidateTemplateLimit = 16;
        private const int PreviewTemplateDrawLimit = 36;
        private const int HugGalleryDrawLimit = 36;
        private const bool TraceFirstIndividualOnly = false;
        private const bool TracePlacementSteps = false;
        private const bool PrebuildComboLibrary = false;
        private const int TraceStepPauseMs = 0;
        private const int GapFillReserveDivisor = 4;
        private const double PairHugGap = 0.5;
        private const double TemplateRatioTolerance = 1e-6;
        private const int TemplateVariantLimit = 8;
        private const int SettleAxisLimit = 8;
        private const int BoardSettlePassLimit = 3;
        private const int PlacementTightLoopLimit = 2;
        private const int PolishSlideSeedLimit = 3;
        private const int NfpSettleCandidateLimit = 64;
        private const int NfpBoundaryCachePointLimit = 96;
        private const int FreeRegionCandidateLimit = 48;
        private const double FreeRegionMinEdge = 18.0;
        private const double FreeRegionCleanTolerance = 2.0;
        private const double NearbyBoundsPadding = 90.0;
        private const int RoundStackCandidateLimit = 72;
        private static readonly int[] Rotations = new int[] { 0, 90, 180, 270 };
        private static DateTime _nextUiPump = DateTime.MinValue;
        private static DateTime _nextPreviewDraw = DateTime.MinValue;
        private static Action<V4Preview> _previewUpdated;
        private static Func<bool> _cancelRequested;
        private static ShapeNestingInput _previewInput;
        private static List<V4Template> _hugGalleryTemplates = new List<V4Template>();
        private static List<int> _hugGalleryTypes = new List<int>();
        private static Dictionary<string, bool> _hugGallerySeen = new Dictionary<string, bool>();
        private static int _runSeed;
        private static Dictionary<string, List<ShapePoint>> _nfpBoundaryCache = new Dictionary<string, List<ShapePoint>>();
        private static Dictionary<string, List<V4FreeRegion>> _freeRegionCache = new Dictionary<string, List<V4FreeRegion>>();
        private static V4Timing _timing = new V4Timing();

        public static ShapeNestingResult Solve(ShapeNestingInput input, Action<string> progress, Action<ShapeNestingResult> candidateUpdated, Action<V4Preview> previewUpdated, Func<bool> cancelRequested)
        {
            _previewUpdated = previewUpdated;
            _previewInput = input;
            _cancelRequested = cancelRequested;
            _runSeed = NewRunSeed();
            _nextPreviewDraw = DateTime.MinValue;
            _hugGalleryTemplates = new List<V4Template>();
            _hugGalleryTypes = new List<int>();
            _hugGallerySeen = new Dictionary<string, bool>();
            _nfpBoundaryCache = new Dictionary<string, List<ShapePoint>>();
            _freeRegionCache = new Dictionary<string, List<V4FreeRegion>>();
            try
            {
                return SolveCore(input, progress, candidateUpdated);
            }
            finally
            {
                _previewUpdated = null;
                _previewInput = null;
                _cancelRequested = null;
                _hugGalleryTemplates = new List<V4Template>();
                _hugGalleryTypes = new List<int>();
                _hugGallerySeen = new Dictionary<string, bool>();
                _nfpBoundaryCache = new Dictionary<string, List<ShapePoint>>();
                _freeRegionCache = new Dictionary<string, List<V4FreeRegion>>();
            }
        }

        private static ShapeNestingResult SolveCore(ShapeNestingInput input, Action<string> progress, Action<ShapeNestingResult> candidateUpdated)
        {
            List<int> order = TypeOrder(input);
            ShapeNestingResult best = null;
            V4Score bestScore = null;
            int populationSize = TraceFirstIndividualOnly ? 1 : BuildStrategies(input, order, 0).Count;
            int generationCount = TraceFirstIndividualOnly ? 1 : Generations;

            if (progress != null)
            {
                progress("\n" + CodexBridgeVersion.Version + " V4 selective-polish experiment: 10 generations, coarse candidate search, NFP/slide polish only after a placement is selected.");
                progress("\nV4 search seed: " + _runSeed.ToString(CultureInfo.InvariantCulture) + ".");
                progress("\nV4 type order: " + FormatTypeOrder(input, order) + ".");
                progress("\nV4 generations: " + generationCount.ToString(CultureInfo.InvariantCulture) + ", individuals per generation: " + populationSize.ToString(CultureInfo.InvariantCulture) + ".");
                if (TraceFirstIndividualOnly)
                {
                    progress("\nV4 trace mode: only gen 0 individual 1, draw candidate parts and board after every placement step.");
                }
            }
            PreviewTemplate(input, order.Count > 0 ? order[0] : 0, SingleTemplate(input.PartTypes[order.Count > 0 ? order[0] : 0].Polygon, 0), "starting");

            for (int generation = 0; generation < generationCount; generation++)
            {
                PumpUi(false);
                List<V4Strategy> strategies = BuildStrategies(input, order, generation);
                int strategyCount = TraceFirstIndividualOnly ? Math.Min(1, strategies.Count) : strategies.Count;
                for (int i = 0; i < strategyCount; i++)
                {
                    PumpUi(false);
                    ShapeNestingResult candidate;
                    _timing = new V4Timing();
                    Stopwatch individualWatch = Stopwatch.StartNew();
                    try
                    {
                        if (progress != null)
                        {
                            progress("\nV4 gen " + generation.ToString(CultureInfo.InvariantCulture) + " individual " + (i + 1).ToString(CultureInfo.InvariantCulture) + "/" + strategies.Count.ToString(CultureInfo.InvariantCulture) + " " + strategies[i].Name + " calculating...");
                        }
                        candidate = PlaceStrategy(input, order, strategies[i], candidateUpdated, generation, i + 1, progress);
                        individualWatch.Stop();
                        ShapeNestingValidator.Validate(candidate);
                    }
                    catch (InvalidOperationException ex)
                    {
                        individualWatch.Stop();
                        if (progress != null)
                        {
                            progress("\nV4 gen " + generation.ToString(CultureInfo.InvariantCulture) + " individual " + (i + 1).ToString(CultureInfo.InvariantCulture) + "/" + strategies.Count.ToString(CultureInfo.InvariantCulture) + " " + strategies[i].Name + " skipped: " + ex.Message + ", elapsed " + individualWatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s, nfp cache " + _nfpBoundaryCache.Count.ToString(CultureInfo.InvariantCulture) + TimingSuffix());
                        }
                        continue;
                    }
                    candidate.Generation = generation;
                    candidate.Individual = i + 1;
                    if (candidateUpdated != null)
                    {
                        candidateUpdated(candidate);
                    }
                    V4Score score = Score(candidate);
                    if (best == null || Compare(score, bestScore) < 0)
                    {
                        best = candidate;
                        bestScore = score;
                    }
                    if (progress != null)
                    {
                        progress("\nV4 gen " + generation.ToString(CultureInfo.InvariantCulture) + " individual " + (i + 1).ToString(CultureInfo.InvariantCulture) + "/" + strategies.Count.ToString(CultureInfo.InvariantCulture) + " " + strategies[i].Name + ": " + Format(candidate) + ", elapsed " + individualWatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s, nfp cache " + _nfpBoundaryCache.Count.ToString(CultureInfo.InvariantCulture) + TimingSuffix());
                    }
                }
            }

            if (best == null)
            {
                throw new InvalidOperationException("V4 did not find a nesting result.");
            }
            if (progress != null)
            {
                progress("\nV4 best selected gen " + best.Generation.ToString(CultureInfo.InvariantCulture) + " individual " + best.Individual.ToString(CultureInfo.InvariantCulture) + ": " + Format(best));
            }
            return best;
        }

        private static int NewRunSeed()
        {
            int tick = Environment.TickCount;
            int utc = (int)(DateTime.UtcNow.Ticks & 0x7fffffff);
            return unchecked(tick * 397 ^ utc);
        }

        public static string Format(ShapeNestingResult result)
        {
            return result.BoardCount.ToString(CultureInfo.InvariantCulture) +
                " sheets, area utilization " + (result.Utilization * 100.0).ToString("0.00", CultureInfo.InvariantCulture) +
                "%, used length " + (UsedLengthRatio(result) * 100.0).ToString("0.00", CultureInfo.InvariantCulture) +
                "%, last right " + result.MinRightRemnant.ToString("0.###", CultureInfo.InvariantCulture) +
                ", total right " + result.TotalRightRemnant.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string TimingSuffix()
        {
            return ", timing {tpl " + V4Timing.Format(_timing.TemplateTicks) + "/" + _timing.TemplateCalls.ToString(CultureInfo.InvariantCulture) +
                ", cand " + V4Timing.Format(_timing.CandidateTicks) + "/" + _timing.CandidateCalls.ToString(CultureInfo.InvariantCulture) +
                " pts " + _timing.CandidatePoints.ToString(CultureInfo.InvariantCulture) +
                ", nfp " + V4Timing.Format(_timing.NfpTicks) + "/" + _timing.NfpCalls.ToString(CultureInfo.InvariantCulture) +
                ", settle " + V4Timing.Format(_timing.SettleTicks) + "/" + _timing.SettleCalls.ToString(CultureInfo.InvariantCulture) +
                ", hit " + V4Timing.Format(_timing.CollisionTicks) + "/" + _timing.CollisionCalls.ToString(CultureInfo.InvariantCulture) +
                ", free " + V4Timing.Format(_timing.FreeRegionTicks) + "/" + _timing.FreeRegionCalls.ToString(CultureInfo.InvariantCulture) +
                " found " + _timing.FreeRegionsFound.ToString(CultureInfo.InvariantCulture) +
                ", holes " + V4Timing.Format(_timing.HoleTicks) + "/" + _timing.HoleCalls.ToString(CultureInfo.InvariantCulture) +
                " found " + _timing.HolesFound.ToString(CultureInfo.InvariantCulture) + "}";
        }

        private static ShapeNestingResult PlaceStrategy(ShapeNestingInput input, List<int> order, V4Strategy strategy, Action<ShapeNestingResult> stepUpdated, int generation, int individual, Action<string> progress)
        {
            ShapeNestingResult result = new ShapeNestingResult();
            result.Input = input;
            result.TotalArea = TotalPartArea(input);
            List<List<ShapePlacement>> boards = new List<List<ShapePlacement>>();
            int[] remaining = Quantities(input);
            int[] nextNumber = InitialNumbers(input);
            Dictionary<string, List<V4Template>> templateCache = new Dictionary<string, List<V4Template>>();
            if (PrebuildComboLibrary)
            {
                PrebuildTemplateLibrary(input, order, strategy, templateCache);
            }

            for (int orderIndex = 0; orderIndex < order.Count; orderIndex++)
            {
                PumpUi(false);
                int typeIndex = order[orderIndex];
                while (remaining[typeIndex] > 0)
                {
                    PumpUi(false);
                    ShapePlacement placed = PlaceMainGroup(input, boards, typeIndex, remaining[typeIndex], nextNumber, strategy, templateCache);
                    if (placed == null)
                    {
                        FillVoids(input, boards, result, remaining, nextNumber, order, orderIndex + 1, strategy, templateCache, stepUpdated, generation, individual, progress);
                        placed = PlaceMainGroup(input, boards, typeIndex, remaining[typeIndex], nextNumber, strategy, templateCache);
                    }
                    if (placed == null)
                    {
                        AddNewBoard(boards);
                        placed = PlaceMainGroupOnBoard(input, boards, boards.Count - 1, typeIndex, remaining[typeIndex], nextNumber, strategy, templateCache);
                        if (placed == null)
                        {
                            placed = PlaceSingleOnNewBoard(input, boards, typeIndex, nextNumber);
                        }
                    }

                    boards[placed.BoardIndex].Add(placed);
                    result.Placements.Add(placed);
                    remaining[typeIndex] -= CountParts(placed);
                    EmitTraceStep(input, boards, stepUpdated, generation, individual,
                        "main " + input.PartTypes[typeIndex].Name + " x" + CountParts(placed).ToString(CultureInfo.InvariantCulture),
                        progress);
                    FillCurrentTypeSingleVoids(input, boards, result, remaining, nextNumber, typeIndex, strategy, templateCache, stepUpdated, generation, individual, progress);
                }

                if (orderIndex + 1 < order.Count)
                {
                    FillVoids(input, boards, result, remaining, nextNumber, order, orderIndex + 1, strategy, templateCache, stepUpdated, generation, individual, progress);
                }
            }

            FillVoids(input, boards, result, remaining, nextNumber, order, 0, strategy, templateCache, stepUpdated, generation, individual, progress);
            TryRepackTrailingTypes(input, boards, order, strategy, templateCache);
            if (FinalBoardSettleAfterSearch)
            {
                SettleBoardsBottomLeft(input, boards);
            }

            CompactBoards(boards);
            RebuildPlacements(result, boards);
            FinalizeResult(input, result, boards);
            return result;
        }

        private static void EmitTraceStep(ShapeNestingInput input, List<List<ShapePlacement>> boards, Action<ShapeNestingResult> stepUpdated, int generation, int individual, string label, Action<string> progress)
        {
            if (!TracePlacementSteps || stepUpdated == null)
            {
                return;
            }
            ShapeNestingResult snapshot = new ShapeNestingResult();
            snapshot.Input = input;
            snapshot.TotalArea = TotalPartArea(input);
            snapshot.Generation = generation;
            snapshot.Individual = individual;
            snapshot.TraceLabel = label;
            RebuildPlacements(snapshot, boards);
            FinalizeResult(input, snapshot, boards);
            if (progress != null)
            {
                progress("\nV4 trace step: " + label + " -> " + Format(snapshot));
            }
            stepUpdated(snapshot);
            TracePause();
        }

        private static void TracePause()
        {
            if (TraceStepPauseMs > 0)
            {
                System.Threading.Thread.Sleep(TraceStepPauseMs);
            }
        }

        private static void PrebuildTemplateLibrary(ShapeNestingInput input, List<int> order, V4Strategy strategy, Dictionary<string, List<V4Template>> templateCache)
        {
            List<V4Template> previewTemplates = new List<V4Template>();
            List<int> previewTypes = new List<int>();
            List<ShapePlacement> emptyBoard = new List<ShapePlacement>();
            foreach (int typeIndex in order)
            {
                PumpUi(false);
                bool preferPairUnits = PreferPairUnits(input.PartTypes[typeIndex].Quantity);
                List<int> counts = HugGalleryCounts(input.PartTypes[typeIndex].Quantity);
                foreach (int count in counts)
                {
                    foreach (V4Template template in BuildTemplatesForCountFast(input, typeIndex, count, strategy, templateCache, preferPairUnits))
                    {
                        if (!TemplateCanParticipateOnBoard(input, emptyBoard, template))
                        {
                            continue;
                        }
                        if (previewTemplates.Count < PreviewTemplateDrawLimit)
                        {
                            previewTemplates.Add(template);
                            previewTypes.Add(typeIndex);
                            ForcePreviewTemplates(input, previewTemplates, previewTypes, "library " + previewTemplates.Count.ToString(CultureInfo.InvariantCulture) + " candidates");
                        }
                    }
                }
            }
            _nextPreviewDraw = DateTime.MinValue;
            ForcePreviewTemplates(input, previewTemplates, previewTypes, "precomputed combo library");
        }

        private static List<int> HugGalleryCounts(int quantity)
        {
            List<int> counts = new List<int>();
            AddCount(counts, 2, quantity);
            return counts;
        }

        private static V4Template PreviewSeedTemplate(ShapeNestingInput input, int typeIndex, int count, V4Strategy strategy)
        {
            ShapePolygon source = input.PartTypes[typeIndex].Polygon;
            if (count <= 1)
            {
                return SingleTemplate(source, strategy.MainRotation);
            }

            List<ShapePolygon> polygons = new List<ShapePolygon>();
            double x = 0.0;
            int previewCount = Math.Min(count, 4);
            for (int i = 0; i < previewCount; i++)
            {
                double rotation = i % 2 == 0 ? strategy.MainRotation : strategy.MainRotation + 180.0;
                ShapePolygon polygon = RotatePolygonNormalize(source, rotation);
                ShapeBounds bounds = polygon.Bounds();
                polygons.Add(polygon.Translate(x - bounds.MinX, -bounds.MinY));
                x += bounds.Width + Gap;
            }
            return NormalizeTemplate(polygons);
        }

        private static ShapePlacement PlaceMainGroup(ShapeNestingInput input, List<List<ShapePlacement>> boards, int typeIndex, int remaining, int[] nextNumber, V4Strategy strategy, Dictionary<string, List<V4Template>> templateCache)
        {
            int boardLimit = Math.Min(boards.Count, ExistingBoardTryLimit);
            for (int boardIndex = 0; boardIndex < boardLimit; boardIndex++)
            {
                ShapePlacement placed = PlaceMainGroupOnBoard(input, boards, boardIndex, typeIndex, remaining, nextNumber, strategy, templateCache);
                if (placed != null)
                {
                    return placed;
                }
            }
            return null;
        }

        private static ShapePlacement PlaceMainGroupOnBoard(ShapeNestingInput input, List<List<ShapePlacement>> boards, int boardIndex, int typeIndex, int remaining, int[] nextNumber, V4Strategy strategy, Dictionary<string, List<V4Template>> templateCache)
        {
            ShapePlacement roundStack = TryPlaceRoundStackGroup(input, boards[boardIndex], boardIndex, typeIndex, remaining, nextNumber, strategy);
            if (roundStack != null)
            {
                return roundStack;
            }

            List<V4Template> batchTemplates = BuildBatchTemplatesForBoard(input, boards[boardIndex], typeIndex, remaining, strategy, templateCache);
            ForcePreviewTemplates(input, typeIndex, batchTemplates, "batch candidates " + input.PartTypes[typeIndex].Name);

            bool preferPairUnits = PreferPairUnits(remaining);
            int selectedCount = int.MinValue;
            while (true)
            {
                int count = NextTemplateCount(batchTemplates, selectedCount, strategy);
                if (count <= 0)
                {
                    break;
                }

                List<V4PlacementChoice> choices = new List<V4PlacementChoice>();
                foreach (V4Template template in batchTemplates)
                {
                    if (template.Count != count)
                    {
                        continue;
                    }
                    foreach (V4Template placementTemplate in PlacementTemplateVariants(template))
                    {
                        ShapePartInstance part = CreateGroupPart(input, typeIndex, placementTemplate, nextNumber[typeIndex]);
                        foreach (ShapePoint candidate in CandidatePoints(input, boards[boardIndex], placementTemplate, false))
                        {
                            PumpUi(false);
                            ShapePlacement placement = MakePlacement(part, placementTemplate, boardIndex, candidate.X, candidate.Y);
                            placement = SettlePlacementBottomLeft(input, boards[boardIndex], placement, placementTemplate);
                            if (!FitsBoard(input, placement.Bounds) || HitsAny(placement, boards[boardIndex]))
                            {
                                continue;
                            }
                            double score = BatchMainScore(input, boards[boardIndex], placement, placementTemplate, strategy, preferPairUnits);
                            choices.Add(new V4PlacementChoice { Placement = placement, Score = score });
                        }
                    }
                }

                ShapePlacement selected = SelectPlacementChoice(choices, strategy, remaining + boardIndex * 1000);
                if (selected != null)
                {
                    selected = PolishSelectedPlacement(input, boards[boardIndex], selected);
                    nextNumber[typeIndex] += CountParts(selected);
                    return selected;
                }
                selectedCount = count;
            }

            return null;
        }

        private static ShapePlacement SelectPlacementChoice(List<V4PlacementChoice> choices, V4Strategy strategy, int generationSalt)
        {
            if (choices == null || choices.Count == 0)
            {
                return null;
            }
            choices.Sort(delegate (V4PlacementChoice a, V4PlacementChoice b)
            {
                return a.Score.CompareTo(b.Score);
            });
            if (strategy == null || strategy.ChoiceSpan <= 1)
            {
                return choices[0].Placement;
            }

            int span = Math.Min(choices.Count, Math.Max(1, strategy.ChoiceSpan));
            int hash = _runSeed;
            hash = unchecked(hash * 16777619) ^ strategy.RandomSalt;
            hash = unchecked(hash * 16777619) ^ generationSalt;
            hash = unchecked(hash * 16777619) ^ choices.Count;
            int index = PositiveModulo(hash, span);
            return choices[index].Placement;
        }

        private static int NextTemplateCount(List<V4Template> templates, int previous, V4Strategy strategy)
        {
            List<int> available = new List<int>();
            foreach (V4Template template in templates)
            {
                if (template == null || available.Contains(template.Count))
                {
                    continue;
                }
                available.Add(template.Count);
            }
            int[] order = CountPreferenceOrder(strategy);
            List<int> orderedAvailable = new List<int>();
            foreach (int preferred in order)
            {
                if (available.Contains(preferred) && !orderedAvailable.Contains(preferred))
                {
                    orderedAvailable.Add(preferred);
                }
            }
            available.Sort();
            available.Reverse();
            foreach (int count in available)
            {
                if (!orderedAvailable.Contains(count))
                {
                    orderedAvailable.Add(count);
                }
            }
            if (previous == int.MinValue)
            {
                return orderedAvailable.Count > 0 ? orderedAvailable[0] : 0;
            }
            int previousIndex = orderedAvailable.IndexOf(previous);
            if (previousIndex >= 0 && previousIndex + 1 < orderedAvailable.Count)
            {
                return orderedAvailable[previousIndex + 1];
            }
            return 0;
        }

        private static int[] CountPreferenceOrder(V4Strategy strategy)
        {
            int mode = strategy == null ? 0 : strategy.CountMode;
            if (mode == 1)
            {
                return new int[] { 4, 6, 2, 1 };
            }
            if (mode == 2)
            {
                return new int[] { 2, 4, 6, 1 };
            }
            if (mode == 3)
            {
                return new int[] { 6, 2, 4, 1 };
            }
            return new int[] { 6, 4, 2, 1 };
        }

        private static List<V4Template> BuildBatchTemplatesForBoard(ShapeNestingInput input, List<ShapePlacement> board, int typeIndex, int remaining, V4Strategy strategy, Dictionary<string, List<V4Template>> templateCache)
        {
            long timingStart = V4Timing.Start();
            List<V4Template> templates = new List<V4Template>();
            try
            {
                bool preferPairUnits = PreferPairUnits(remaining);
                List<int> counts = PreferredMainCounts(input, board, typeIndex, remaining, strategy);
                int perCountLimit = Math.Max(2, BatchCandidateTemplateLimit / Math.Max(1, counts.Count));
                foreach (int count in counts)
                {
                    int limitForCount = templates.Count + perCountLimit;
                    AddParticipatingTemplates(input, board, templates, typeIndex, count, strategy, templateCache, limitForCount, preferPairUnits);
                }
                SortBatchTemplates(input, typeIndex, templates, strategy, preferPairUnits);
                if (templates.Count > BatchCandidateTemplateLimit)
                {
                    templates.RemoveRange(BatchCandidateTemplateLimit, templates.Count - BatchCandidateTemplateLimit);
                }
                return templates;
            }
            finally
            {
                _timing.TemplateTicks += V4Timing.Elapsed(timingStart);
                _timing.TemplateCalls++;
            }
        }

        private static void AddParticipatingTemplates(ShapeNestingInput input, List<ShapePlacement> board, List<V4Template> templates, int typeIndex, int count, V4Strategy strategy, Dictionary<string, List<V4Template>> templateCache, int limit, bool fastPairCounts)
        {
            if (count <= 0)
            {
                return;
            }
            foreach (V4Template template in BuildTemplatesForCountFast(input, typeIndex, count, strategy, templateCache, fastPairCounts))
            {
                PumpUi(false);
                if (!TemplateCanParticipateOnBoard(input, board, template))
                {
                    continue;
                }
                AddTemplate(templates, template);
                if (templates.Count >= limit)
                {
                    return;
                }
            }
        }

        private static void SortBatchTemplates(ShapeNestingInput input, int typeIndex, List<V4Template> templates, V4Strategy strategy, bool preferPairUnits)
        {
            templates.Sort(delegate (V4Template a, V4Template b)
            {
                int cmp = b.Count.CompareTo(a.Count);
                if (cmp != 0)
                {
                    return cmp;
                }
                cmp = CompareTemplateShapePreference(a, b, strategy);
                if (cmp != 0)
                {
                    return cmp;
                }
                double partArea = Math.Max(1.0, input.PartTypes[typeIndex].Polygon.Area());
                cmp = CompareDescending(TemplateOccupiedRatio(a, partArea), TemplateOccupiedRatio(b, partArea), TemplateRatioTolerance);
                if (cmp != 0)
                {
                    return cmp;
                }
                cmp = CompareDescending(a.Bounds.Width * a.Bounds.Height, b.Bounds.Width * b.Bounds.Height, 1e-6);
                if (cmp != 0)
                {
                    return cmp;
                }
                return TemplateScore(a, strategy).CompareTo(TemplateScore(b, strategy));
            });
        }

        private static int CompareTemplateShapePreference(V4Template a, V4Template b, V4Strategy strategy)
        {
            int mode = strategy == null ? 0 : strategy.ShapeMode;
            if (mode == 0)
            {
                return 0;
            }
            double aAspect = TemplateAspect(a);
            double bAspect = TemplateAspect(b);
            if (mode == 1)
            {
                return aAspect.CompareTo(bAspect);
            }
            if (mode == 2)
            {
                return bAspect.CompareTo(aAspect);
            }
            if (mode == 3)
            {
                double aArea = a.Bounds.Width * a.Bounds.Height;
                double bArea = b.Bounds.Width * b.Bounds.Height;
                return CompareDescending(aArea, bArea, 1e-6);
            }
            return 0;
        }

        private static double TemplateAspect(V4Template template)
        {
            if (template == null)
            {
                return double.PositiveInfinity;
            }
            double longSide = Math.Max(template.Bounds.Width, template.Bounds.Height);
            double shortSide = Math.Max(1.0, Math.Min(template.Bounds.Width, template.Bounds.Height));
            return longSide / shortSide;
        }

        private static bool IsRoundLikePart(ShapePolygon polygon)
        {
            if (polygon == null || polygon.Points == null || polygon.Points.Count < 8)
            {
                return false;
            }
            ShapeBounds bounds = polygon.Bounds();
            double width = Math.Max(1.0, bounds.Width);
            double height = Math.Max(1.0, bounds.Height);
            double aspect = Math.Max(width, height) / Math.Min(width, height);
            if (aspect > 1.28)
            {
                return false;
            }

            double boxArea = width * height;
            double ratio = Math.Abs(polygon.Area()) / Math.Max(1.0, boxArea);
            if (ratio < 0.58 || ratio > 0.88)
            {
                return false;
            }

            ShapePoint center = new ShapePoint((bounds.MinX + bounds.MaxX) / 2.0, (bounds.MinY + bounds.MaxY) / 2.0);
            double minRadius = double.PositiveInfinity;
            double maxRadius = 0.0;
            foreach (ShapePoint point in polygon.Points)
            {
                double dx = point.X - center.X;
                double dy = point.Y - center.Y;
                double radius = Math.Sqrt(dx * dx + dy * dy);
                minRadius = Math.Min(minRadius, radius);
                maxRadius = Math.Max(maxRadius, radius);
            }
            if (minRadius <= 1e-6)
            {
                return false;
            }
            return maxRadius / minRadius <= 1.45;
        }

        private static bool PreferPairUnits(int remaining)
        {
            return remaining >= 2;
        }

        private static List<int> PreferredMainCounts(ShapeNestingInput input, List<ShapePlacement> board, int typeIndex, int remaining, V4Strategy strategy)
        {
            return PreferredCountsFromMax(remaining, EstimateBatchCount(input, board, typeIndex, remaining, strategy), PreferPairUnits(remaining));
        }

        private static List<int> PreferredCountsFromMax(int remaining, int maxCount, bool preferPairUnits)
        {
            List<int> counts = new List<int>();
            maxCount = Math.Min(remaining, Math.Min(MaxComboBlockSize, maxCount));
            if (maxCount <= 0)
            {
                return counts;
            }

            if (preferPairUnits)
            {
                AddCount(counts, 6, maxCount);
                AddCount(counts, 4, maxCount);
                AddCount(counts, 2, maxCount);
                AddCount(counts, 1, remaining);
                return counts;
            }

            for (int count = maxCount; count >= 1; count--)
            {
                AddCount(counts, count, remaining);
            }
            return counts;
        }

        private static void AddCount(List<int> counts, int count, int remaining)
        {
            if (count <= 0 || count > remaining || counts.Contains(count))
            {
                return;
            }
            counts.Add(count);
        }

        private static int EstimateBatchCount(ShapeNestingInput input, List<ShapePlacement> board, int typeIndex, int remaining, V4Strategy strategy)
        {
            if (remaining <= 0)
            {
                return 0;
            }

            double usedX = UsedX(board);
            double usedY = UsedY(board);
            ShapePolygon source = input.PartTypes[typeIndex].Polygon;
            double partArea = Math.Max(1.0, Math.Abs(source.Area()));
            ShapeBounds single = MinimumBounds(source);
            int estimate;
            if (board.Count == 0)
            {
                estimate = EstimateRegionCount(input.BoardWidth, input.BoardHeight, partArea, single, strategy);
            }
            else
            {
                double rightWidth = Math.Max(0.0, input.BoardWidth - usedX - Gap);
                double topHeight = Math.Max(0.0, input.BoardHeight - usedY - Gap);
                int rightEstimate = EstimateRegionCount(rightWidth, input.BoardHeight, partArea, single, strategy);
                int topEstimate = EstimateRegionCount(Math.Max(0.0, usedX), topHeight, partArea, single, strategy);
                estimate = Math.Max(rightEstimate, topEstimate);
            }
            return Math.Min(remaining, Math.Min(MaxComboBlockSize, estimate));
        }

        private static int EstimateRegionCount(double regionWidth, double regionHeight, double partArea, ShapeBounds single, V4Strategy strategy)
        {
            if (regionWidth <= 1e-6 || regionHeight <= 1e-6)
            {
                return 0;
            }

            int byArea = (int)Math.Floor(regionWidth * regionHeight / Math.Max(1.0, partArea));
            if (byArea <= 0)
            {
                return 0;
            }

            int columns = Math.Max(0, (int)Math.Floor((regionWidth + Gap) / Math.Max(1.0, single.Width + Gap)));
            int rows = Math.Max(0, (int)Math.Floor((regionHeight + Gap) / Math.Max(1.0, single.Height + Gap)));
            int byGrid = columns * rows;
            int estimate = Math.Max(1, Math.Min(byArea, Math.Max(byGrid, strategy.GroupSize)));
            return estimate;
        }

        private static ShapeBounds MinimumBounds(ShapePolygon source)
        {
            List<ShapePolygon> polygons = new List<ShapePolygon> { source };
            ShapeBounds best = null;
            double bestArea = double.PositiveInfinity;
            foreach (double angle in TemplatePlacementAngles(polygons))
            {
                ShapeBounds bounds = BoundsOf(RotateAndNormalize(polygons, angle));
                double area = bounds.Width * bounds.Height;
                if (area < bestArea)
                {
                    bestArea = area;
                    best = bounds;
                }
            }
            return best ?? source.Bounds();
        }

        private static bool TemplateCanParticipateOnBoard(ShapeNestingInput input, List<ShapePlacement> board, V4Template template)
        {
            foreach (V4Template variant in PlacementTemplateVariants(template))
            {
                if (variant.Bounds.Width > input.BoardWidth + 1e-6 || variant.Bounds.Height > input.BoardHeight + 1e-6)
                {
                    continue;
                }
                if (board.Count == 0)
                {
                    return true;
                }
                double usedX = UsedX(board);
                double usedY = UsedY(board);
                double rightWidth = Math.Max(0.0, input.BoardWidth - usedX - Gap);
                if (TemplateFitsRemainingRegion(variant, rightWidth, input.BoardHeight))
                {
                    return true;
                }
                double topHeight = Math.Max(0.0, input.BoardHeight - usedY - Gap);
                if (TemplateFitsRemainingRegion(variant, Math.Max(0.0, usedX), topHeight))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool TemplateFitsRemainingRegion(V4Template template, double regionLength, double regionWidth)
        {
            if (template == null || regionLength <= 1e-6 || regionWidth <= 1e-6)
            {
                return false;
            }
            return template.Bounds.Width <= regionLength + Gap * 2.0 &&
                template.Bounds.Height <= regionWidth + Gap * 2.0;
        }

        private static double BatchMainScore(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement placement, V4Template template, V4Strategy strategy, bool preferPairUnits)
        {
            double boardArea = input.BoardWidth * input.BoardHeight;
            double usedX = UsedX(board);
            double regionWidth = board.Count == 0 ? input.BoardWidth : Math.Max(0.0, input.BoardWidth - usedX - Gap);
            double regionArea = Math.Max(1.0, regionWidth * input.BoardHeight);
            double regionScale = Math.Min(1.0, regionArea / Math.Max(1.0, boardArea));
            double ratioReward = TemplateOccupiedRatio(template, Math.Max(1.0, input.PartTypes[placement.Part.TypeIndex].Polygon.Area())) * boardArea * 900.0;
            double countReward = template.Count * boardArea * (120.0 + 650.0 * regionScale);
            double boxReward = Math.Min(template.Bounds.Width * template.Bounds.Height, regionArea) * (600.0 + 600.0 * regionScale);
            double hugUnitReward = preferPairUnits ? (template.Count / 2.0) * boardArea * 120.0 : 0.0;
            double shapeBias = TemplateShapeSelectionBias(input, template, strategy);
            return MainScore(input, board, placement, template, strategy) -
                shapeBias -
                ExplorationBias(input, placement, template, strategy) -
                countReward -
                boxReward -
                hugUnitReward -
                ratioReward;
        }

        private static double ExplorationBias(ShapeNestingInput input, ShapePlacement placement, V4Template template, V4Strategy strategy)
        {
            if (input == null || placement == null || template == null || strategy == null || strategy.RandomWeight <= 0.0)
            {
                return 0.0;
            }
            double boardArea = Math.Max(1.0, input.BoardWidth * input.BoardHeight);
            int hash = _runSeed;
            hash = unchecked(hash * 16777619) ^ strategy.RandomSalt;
            hash = unchecked(hash * 16777619) ^ template.Count;
            hash = unchecked(hash * 16777619) ^ (int)Math.Round(template.Bounds.Width * 10.0);
            hash = unchecked(hash * 16777619) ^ (int)Math.Round(template.Bounds.Height * 10.0);
            hash = unchecked(hash * 16777619) ^ (int)Math.Round(placement.Bounds.MinX * 10.0);
            hash = unchecked(hash * 16777619) ^ (int)Math.Round(placement.Bounds.MinY * 10.0);
            double value = PositiveUnitHash(hash);
            return value * boardArea * strategy.RandomWeight;
        }

        private static double PositiveUnitHash(int value)
        {
            uint x = unchecked((uint)value);
            x ^= x >> 16;
            x *= 2246822519u;
            x ^= x >> 13;
            x *= 3266489917u;
            x ^= x >> 16;
            return (x & 0xFFFFFF) / (double)0x1000000;
        }

        private static double TemplateShapeSelectionBias(ShapeNestingInput input, V4Template template, V4Strategy strategy)
        {
            if (input == null || template == null || strategy == null)
            {
                return 0.0;
            }
            double boardArea = Math.Max(1.0, input.BoardWidth * input.BoardHeight);
            double aspect = TemplateAspect(template);
            if (strategy.ShapeMode == 1)
            {
                return Math.Max(0.0, 8.0 - aspect) * boardArea * 180.0;
            }
            if (strategy.ShapeMode == 2)
            {
                return Math.Max(0.0, aspect - 1.0) * boardArea * 180.0;
            }
            if (strategy.ShapeMode == 3)
            {
                return template.Bounds.Width * template.Bounds.Height * 260.0;
            }
            return 0.0;
        }

        private static ShapePlacement PlaceSingleOnNewBoard(ShapeNestingInput input, List<List<ShapePlacement>> boards, int typeIndex, int[] nextNumber)
        {
            AddNewBoard(boards);
            V4Template template = SingleTemplate(input.PartTypes[typeIndex].Polygon, 0);
            ShapePartInstance part = CreateGroupPart(input, typeIndex, template, nextNumber[typeIndex]);
            nextNumber[typeIndex]++;
            return MakePlacement(part, template, boards.Count - 1, 0.0, 0.0);
        }

        private static ShapePlacement TryPlaceRoundStackGroup(ShapeNestingInput input, List<ShapePlacement> board, int boardIndex, int typeIndex, int remaining, int[] nextNumber, V4Strategy strategy)
        {
            if (remaining <= 0 || !IsRoundLikePart(input.PartTypes[typeIndex].Polygon))
            {
                return null;
            }

            List<V4Template> templates = RoundStackTemplates(input, typeIndex, remaining, strategy);
            ShapePlacement best = null;
            double bestScore = double.PositiveInfinity;
            foreach (V4Template template in templates)
            {
                ShapePartInstance part = CreateGroupPart(input, typeIndex, template, nextNumber[typeIndex]);
                foreach (ShapePoint point in RoundStackCandidatePoints(input, board, template))
                {
                    PumpUi(false);
                    ShapePlacement placement = MakePlacement(part, template, boardIndex, point.X, point.Y);
                    placement = SettlePlacementBottomLeft(input, board, placement, template);
                    if (!FitsBoard(input, placement.Bounds) || HitsAny(placement, board))
                    {
                        continue;
                    }
                    double score = RoundStackScore(input, board, placement, template, strategy);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = placement;
                    }
                }
            }
            if (best != null)
            {
                best = PolishSelectedPlacement(input, board, best);
                nextNumber[typeIndex] += CountParts(best);
            }
            return best;
        }

        private static List<V4Template> RoundStackTemplates(ShapeNestingInput input, int typeIndex, int remaining, V4Strategy strategy)
        {
            List<V4Template> templates = new List<V4Template>();
            ShapePolygon source = input.PartTypes[typeIndex].Polygon;
            ShapeBounds bounds = MinimumBounds(source);
            int columns = Math.Max(1, (int)Math.Floor((input.BoardWidth + Gap) / Math.Max(1.0, bounds.Width + Gap)));
            int rows = Math.Max(1, (int)Math.Floor((input.BoardHeight + Gap) / Math.Max(1.0, bounds.Height + Gap)));
            int maxWide = Math.Min(remaining, Math.Min(MaxComboBlockSize, columns));
            int maxTall = Math.Min(remaining, Math.Min(MaxComboBlockSize, rows));
            int preferredWide = RoundPreferredCount(remaining, maxWide);
            int preferredTall = RoundPreferredCount(remaining, maxTall);

            AddRoundTemplate(templates, source, preferredWide, true, strategy);
            AddRoundTemplate(templates, source, Math.Min(preferredWide, 4), true, strategy);
            AddRoundTemplate(templates, source, 2, true, strategy);
            AddRoundTemplate(templates, source, 1, true, strategy);
            AddRoundTemplate(templates, source, preferredTall, false, strategy);
            AddRoundTemplate(templates, source, Math.Min(preferredTall, 4), false, strategy);
            AddRoundTemplate(templates, source, 2, false, strategy);
            AddRoundTemplate(templates, source, 1, false, strategy);
            return templates;
        }

        private static int RoundPreferredCount(int remaining, int maxCount)
        {
            maxCount = Math.Min(remaining, Math.Min(MaxComboBlockSize, maxCount));
            if (maxCount >= 6)
            {
                return 6;
            }
            if (maxCount >= 4)
            {
                return 4;
            }
            if (maxCount >= 2)
            {
                return 2;
            }
            return Math.Max(1, maxCount);
        }

        private static void AddRoundTemplate(List<V4Template> templates, ShapePolygon source, int count, bool horizontal, V4Strategy strategy)
        {
            if (count <= 0)
            {
                return;
            }
            int rotation = strategy == null ? 0 : strategy.MainRotation;
            AddTemplate(templates, RowTemplate(source, count, rotation, horizontal));
        }

        private static List<ShapePoint> RoundStackCandidatePoints(ShapeNestingInput input, List<ShapePlacement> board, V4Template template)
        {
            List<ShapePoint> points = new List<ShapePoint>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            AddCandidate(points, seen, 0.0, 0.0);
            if (board.Count == 0)
            {
                return points;
            }

            double usedX = UsedX(board);
            double maxX = Math.Min(input.BoardWidth - template.Bounds.Width, Math.Max(usedX + template.Bounds.Width * 1.25, template.Bounds.Width));
            AddCandidate(points, seen, Math.Max(0.0, usedX + Gap), 0.0);
            foreach (ShapePlacement existing in board)
            {
                AddCandidate(points, seen, existing.Bounds.MaxX + Gap, 0.0);
                AddCandidate(points, seen, existing.Bounds.MinX, existing.Bounds.MaxY + Gap);
                AddCandidate(points, seen, Math.Max(0.0, existing.Bounds.MaxX - template.Bounds.Width), existing.Bounds.MinY);
                AddCandidate(points, seen, existing.Bounds.MinX, Math.Max(0.0, existing.Bounds.MaxY - template.Bounds.Height));
            }

            double stepX = Math.Max(8.0, Math.Min(template.Bounds.Width + Gap, 80.0));
            double stepY = Math.Max(8.0, Math.Min(template.Bounds.Height + Gap, 80.0));
            for (double y = 0.0; y <= input.BoardHeight - template.Bounds.Height + 1e-6 && points.Count < RoundStackCandidateLimit; y += stepY)
            {
                for (double x = 0.0; x <= maxX + 1e-6 && points.Count < RoundStackCandidateLimit; x += stepX)
                {
                    AddCandidate(points, seen, x, y);
                }
            }

            points.Sort(delegate (ShapePoint a, ShapePoint b)
            {
                int cmp = a.Y.CompareTo(b.Y);
                return cmp != 0 ? cmp : a.X.CompareTo(b.X);
            });
            return points;
        }

        private static double RoundStackScore(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement placement, V4Template template, V4Strategy strategy)
        {
            double usedX = UsedX(board);
            double addedX = Math.Max(0.0, placement.Bounds.MaxX - usedX);
            double contact = FastBoundsContactReward(board, placement.Bounds);
            double near = FastBoundsNearReward(board, placement.Bounds, 40.0);
            double countReward = template.Count * input.BoardWidth * input.BoardHeight * 250.0;
            double random = ExplorationBias(input, placement, template, strategy) * 0.15;
            return placement.Bounds.MinY * 50000000.0 +
                placement.Bounds.MinX * 3200000.0 +
                addedX * 4200000.0 +
                placement.Bounds.MaxY * 50000.0 -
                contact * 1800000.0 -
                near * 600000.0 -
                countReward -
                random;
        }

        private static void FillVoids(ShapeNestingInput input, List<List<ShapePlacement>> boards, ShapeNestingResult result, int[] remaining, int[] nextNumber, List<int> order, int smallerStart, V4Strategy strategy, Dictionary<string, List<V4Template>> templateCache, Action<ShapeNestingResult> stepUpdated, int generation, int individual, Action<string> progress)
        {
            if (boards.Count == 0)
            {
                return;
            }

            int passes = 0;
            bool placedAny = true;
            while (placedAny && passes < FillPassLimit)
            {
                PumpUi(false);
                placedAny = false;
                passes++;
                for (int boardIndex = 0; boardIndex < boards.Count; boardIndex++)
                {
                    PumpUi(false);
                    ShapePlacement bandFill = BestBandFill(input, boards[boardIndex], boardIndex, remaining, nextNumber, order, smallerStart, strategy, templateCache);
                    if (bandFill != null)
                    {
                        boards[bandFill.BoardIndex].Add(bandFill);
                        result.Placements.Add(bandFill);
                        remaining[bandFill.Part.TypeIndex] -= CountParts(bandFill);
                        nextNumber[bandFill.Part.TypeIndex] += CountParts(bandFill);
                        placedAny = true;
                        EmitTraceStep(input, boards, stepUpdated, generation, individual,
                            "band fill " + input.PartTypes[bandFill.Part.TypeIndex].Name + " x" + CountParts(bandFill).ToString(CultureInfo.InvariantCulture),
                            progress);
                        continue;
                    }
                    foreach (V4Hole hole in FindHoles(input, boards[boardIndex]))
                    {
                        ShapePlacement fill = BestFillForHole(input, boards[boardIndex], boardIndex, remaining, nextNumber, order, smallerStart, hole, strategy, templateCache);
                        if (fill != null)
                        {
                            boards[fill.BoardIndex].Add(fill);
                            result.Placements.Add(fill);
                            remaining[fill.Part.TypeIndex] -= CountParts(fill);
                            nextNumber[fill.Part.TypeIndex] += CountParts(fill);
                            placedAny = true;
                            EmitTraceStep(input, boards, stepUpdated, generation, individual,
                                "fill " + input.PartTypes[fill.Part.TypeIndex].Name + " x" + CountParts(fill).ToString(CultureInfo.InvariantCulture) +
                                (hole.Internal ? " internal" : " right"),
                                progress);
                            break;
                        }
                    }
                }
            }
        }

        private static void FillCurrentTypeSingleVoids(ShapeNestingInput input, List<List<ShapePlacement>> boards, ShapeNestingResult result, int[] remaining, int[] nextNumber, int typeIndex, V4Strategy strategy, Dictionary<string, List<V4Template>> templateCache, Action<ShapeNestingResult> stepUpdated, int generation, int individual, Action<string> progress)
        {
            if (remaining[typeIndex] <= 0 || boards.Count == 0)
            {
                return;
            }
            if (IsRoundLikePart(input.PartTypes[typeIndex].Polygon) && remaining[typeIndex] > 2)
            {
                return;
            }

            int passes = 0;
            bool placedAny = true;
            while (placedAny && passes < CurrentTypeFillPassLimit && remaining[typeIndex] > 0)
            {
                PumpUi(false);
                placedAny = false;
                passes++;
                for (int boardIndex = 0; boardIndex < boards.Count && remaining[typeIndex] > 0; boardIndex++)
                {
                    PumpUi(false);
                    foreach (V4Hole hole in FindHoles(input, boards[boardIndex]))
                    {
                        ShapePlacement fill = BestSingleTypeFillForHole(input, boards[boardIndex], boardIndex, remaining, nextNumber, typeIndex, hole, strategy, templateCache);
                        if (fill == null)
                        {
                            continue;
                        }

                        boards[fill.BoardIndex].Add(fill);
                        result.Placements.Add(fill);
                        remaining[typeIndex] -= CountParts(fill);
                        nextNumber[typeIndex] += CountParts(fill);
                        placedAny = true;
                        EmitTraceStep(input, boards, stepUpdated, generation, individual,
                            "same-type fill " + input.PartTypes[typeIndex].Name + " x" + CountParts(fill).ToString(CultureInfo.InvariantCulture) +
                            (hole.Internal ? " internal" : " right"),
                            progress);
                        break;
                    }
                }
            }
        }

        private static ShapePlacement BestSingleTypeBandFill(ShapeNestingInput input, List<ShapePlacement> board, int boardIndex, int[] remaining, int[] nextNumber, int typeIndex, V4Strategy strategy, Dictionary<string, List<V4Template>> templateCache)
        {
            if (remaining[typeIndex] <= 0)
            {
                return null;
            }

            List<int> oneTypeOrder = new List<int> { typeIndex };
            return BestBandFill(input, board, boardIndex, remaining, nextNumber, oneTypeOrder, 0, strategy, templateCache);
        }

        private static ShapePlacement BestBandFill(ShapeNestingInput input, List<ShapePlacement> board, int boardIndex, int[] remaining, int[] nextNumber, List<int> order, int smallerStart, V4Strategy strategy, Dictionary<string, List<V4Template>> templateCache)
        {
            if (board.Count == 0)
            {
                return null;
            }

            double usedX = UsedX(board);
            if (usedX <= 1.0)
            {
                return null;
            }

            ShapePlacement best = null;
            double bestScore = double.PositiveInfinity;
            int checkedTypes = 0;
            for (int i = Math.Max(0, smallerStart); i < order.Count && checkedTypes < FillTypeLimit; i++)
            {
                int typeIndex = order[i];
                if (remaining[typeIndex] <= 0)
                {
                    continue;
                }

                checkedTypes++;
                foreach (int count in BandCandidateCounts(input, board, typeIndex, remaining[typeIndex], strategy))
                {
                    bool fastPairCounts = PreferPairUnits(remaining[typeIndex]);
                    foreach (V4Template template in BuildTemplatesForCountFast(input, typeIndex, count, strategy, templateCache, fastPairCounts))
                    {
                        foreach (V4Template placementTemplate in PlacementTemplateVariants(template))
                        {
                            if (placementTemplate.Bounds.Width > usedX + 1e-6 || placementTemplate.Bounds.Height > input.BoardHeight + 1e-6)
                            {
                                continue;
                            }

                            ShapePartInstance part = CreateGroupPart(input, typeIndex, placementTemplate, nextNumber[typeIndex]);
                            foreach (ShapePoint point in BandCandidatePoints(input, board, placementTemplate, usedX))
                            {
                                PumpUi(false);
                                ShapePlacement seed = MakePlacement(part, placementTemplate, boardIndex, point.X, point.Y);
                                ShapePlacement settled = SettlePlacementBottomLeft(input, board, seed, placementTemplate);
                                if (settled.Bounds.MaxX > usedX + 1e-6 || !FitsBoard(input, settled.Bounds) || HitsAny(settled, board))
                                {
                                    continue;
                                }

                                double score = BandFillScore(input, board, settled, placementTemplate, usedX, i);
                                if (score < bestScore)
                                {
                                    bestScore = score;
                                    best = settled;
                                }
                            }
                        }
                    }
                }
            }
            return best;
        }

        private static List<int> BandCandidateCounts(ShapeNestingInput input, List<ShapePlacement> board, int typeIndex, int remaining, V4Strategy strategy)
        {
            if (remaining <= 0)
            {
                return new List<int>();
            }

            double usedX = Math.Max(1.0, UsedX(board));
            ShapePolygon source = input.PartTypes[typeIndex].Polygon;
            double partArea = Math.Max(1.0, Math.Abs(source.Area()));
            int byArea = Math.Max(1, (int)Math.Floor(usedX * input.BoardHeight / partArea));
            ShapeBounds single = MinimumBounds(source);
            int columns = Math.Max(1, (int)Math.Floor((usedX + Gap) / Math.Max(1.0, single.Width + Gap)));
            int rows = Math.Max(1, (int)Math.Floor((input.BoardHeight + Gap) / Math.Max(1.0, single.Height + Gap)));
            int byGrid = Math.Max(1, columns * rows);
            int maxCount = Math.Min(remaining, Math.Min(MaxComboBlockSize, Math.Max(strategy.GroupSize, Math.Min(byArea, byGrid))));
            return PreferredCountsFromMax(remaining, maxCount, PreferPairUnits(remaining));
        }

        private static List<ShapePoint> BandCandidatePoints(ShapeNestingInput input, List<ShapePlacement> board, V4Template template, double usedX)
        {
            List<ShapePoint> points = new List<ShapePoint>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            double maxX = Math.Max(0.0, Math.Min(usedX - template.Bounds.Width, input.BoardWidth - template.Bounds.Width));
            double maxY = Math.Max(0.0, input.BoardHeight - template.Bounds.Height);
            AddCandidate(points, seen, 0.0, 0.0);
            AddCandidate(points, seen, maxX, 0.0);
            AddCandidate(points, seen, 0.0, maxY);
            AddCandidate(points, seen, maxX, maxY);
            foreach (ShapePlacement existing in board)
            {
                AddCandidate(points, seen, existing.Bounds.MinX, existing.Bounds.MaxY + Gap);
                AddCandidate(points, seen, Math.Max(0.0, existing.Bounds.MaxX - template.Bounds.Width), existing.Bounds.MaxY + Gap);
                AddCandidate(points, seen, existing.Bounds.MaxX + Gap, existing.Bounds.MinY);
                AddCandidate(points, seen, Math.Max(0.0, existing.Bounds.MinX - template.Bounds.Width - Gap), existing.Bounds.MinY);
                AddCandidate(points, seen, existing.Bounds.MinX, Math.Max(0.0, existing.Bounds.MinY - template.Bounds.Height - Gap));
                AddCandidate(points, seen, Math.Max(0.0, existing.Bounds.MaxX - template.Bounds.Width), Math.Max(0.0, existing.Bounds.MinY - template.Bounds.Height - Gap));
            }

            List<ShapePoint> clipped = new List<ShapePoint>();
            Dictionary<string, bool> clippedSeen = new Dictionary<string, bool>();
            foreach (ShapePoint point in points)
            {
                double x = Math.Max(0.0, Math.Min(maxX, point.X));
                double y = Math.Max(0.0, Math.Min(maxY, point.Y));
                AddCandidate(clipped, clippedSeen, x, y);
            }
            return CoarseFilterCandidatePoints(input, board, template, LimitPoints(clipped, FillCandidateLimit), true);
        }

        private static double BandFillScore(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement placement, V4Template template, double usedX, int typeRank)
        {
            double contact = FastBoundsContactReward(board, placement.Bounds);
            double near = FastBoundsNearReward(board, placement.Bounds, 70.0);
            double rightGap = Math.Max(0.0, usedX - placement.Bounds.MaxX);
            double topGap = Math.Max(0.0, input.BoardHeight - placement.Bounds.MaxY);
            double addedX = Math.Max(0.0, placement.Bounds.MaxX - usedX);
            double highWindowReward = placement.Bounds.MinY * input.BoardWidth * 600.0 +
                rightGap * input.BoardHeight * 180.0;
            return addedX * 900000000.0 +
                topGap * 12000.0 +
                placement.Bounds.MinX * 1200.0 +
                placement.Bounds.MinY * 200.0 +
                typeRank * 150000.0 -
                highWindowReward -
                CountParts(placement) * input.BoardWidth * input.BoardHeight * 80.0 -
                contact * 900000.0 -
                near * 250000.0;
        }

        private static ShapePlacement BestSingleTypeFillForHole(ShapeNestingInput input, List<ShapePlacement> board, int boardIndex, int[] remaining, int[] nextNumber, int typeIndex, V4Hole hole, V4Strategy strategy, Dictionary<string, List<V4Template>> templateCache)
        {
            if (remaining[typeIndex] <= 0)
            {
                return null;
            }

            ShapePlacement best = null;
            double bestScore = double.PositiveInfinity;
            List<int> counts = FillCandidateCounts(input, hole, typeIndex, remaining[typeIndex], strategy);
            bool fastPairCounts = PreferPairUnits(remaining[typeIndex]);
            foreach (int count in counts)
            {
                foreach (V4Template template in BuildTemplatesForCountFast(input, typeIndex, count, strategy, templateCache, fastPairCounts))
                {
                    foreach (V4Template placementTemplate in PlacementTemplateVariants(template))
                    {
                        if (!TemplateFitsHoleDirect(placementTemplate, hole))
                        {
                            continue;
                        }
                        ShapePartInstance part = CreateGroupPart(input, typeIndex, placementTemplate, nextNumber[typeIndex]);
                        ShapePlacement placement = BestPlacementNearHole(input, board, part, placementTemplate, boardIndex, hole, strategy);
                        if (placement == null)
                        {
                            continue;
                        }
                        double score = FillScore(input, board, placement, placementTemplate, hole, 0);
                        if (score < bestScore)
                        {
                            bestScore = score;
                            best = placement;
                        }
                    }
                }
            }
            return best;
        }

        private static ShapePlacement BestFillForHole(ShapeNestingInput input, List<ShapePlacement> board, int boardIndex, int[] remaining, int[] nextNumber, List<int> order, int smallerStart, V4Hole hole, V4Strategy strategy, Dictionary<string, List<V4Template>> templateCache)
        {
            ShapePlacement best = null;
            double bestScore = double.PositiveInfinity;
            int checkedTypes = 0;
            List<V4Template> previewTemplates = new List<V4Template>();
            List<int> previewTypes = new List<int>();
            for (int i = smallerStart; i < order.Count && checkedTypes < FillTypeLimit; i++)
            {
                int typeIndex = order[i];
                if (remaining[typeIndex] <= 0)
                {
                    continue;
                }
                if (IsRoundLikePart(input.PartTypes[typeIndex].Polygon))
                {
                    checkedTypes++;
                    ShapePlacement roundFill = BestRoundFillForHole(input, board, boardIndex, remaining, nextNumber, typeIndex, hole, strategy);
                    if (roundFill != null)
                    {
                        double roundScore = RoundHoleFillScore(input, board, roundFill, hole, i);
                        if (roundScore < bestScore)
                        {
                            bestScore = roundScore;
                            best = roundFill;
                        }
                    }
                    continue;
                }
                checkedTypes++;
                List<int> counts = FillCandidateCounts(input, hole, typeIndex, remaining[typeIndex], strategy);
                bool fastPairCounts = PreferPairUnits(remaining[typeIndex]);
                foreach (int count in counts)
                {
                    foreach (V4Template template in BuildTemplatesForCountFast(input, typeIndex, count, strategy, templateCache, fastPairCounts))
                    {
                        if (previewTemplates.Count < PreviewTemplateDrawLimit)
                        {
                            previewTemplates.Add(template);
                            previewTypes.Add(typeIndex);
                        }
                        foreach (V4Template placementTemplate in PlacementTemplateVariants(template))
                        {
                            if (!TemplateFitsHoleDirect(placementTemplate, hole))
                            {
                                continue;
                            }
                            ShapePartInstance part = CreateGroupPart(input, typeIndex, placementTemplate, nextNumber[typeIndex]);
                            ShapePlacement placement = BestPlacementNearHole(input, board, part, placementTemplate, boardIndex, hole, strategy);
                            if (placement == null)
                            {
                                continue;
                            }
                            double score = FillScore(input, board, placement, placementTemplate, hole, i);
                            if (score < bestScore)
                            {
                                bestScore = score;
                                best = placement;
                            }
                        }
                    }
                }
            }
            ForcePreviewTemplates(input, previewTemplates, previewTypes, "fill candidates");
            return PolishSelectedHolePlacement(input, board, best, hole);
        }

        private static ShapePlacement BestRoundFillForHole(ShapeNestingInput input, List<ShapePlacement> board, int boardIndex, int[] remaining, int[] nextNumber, int typeIndex, V4Hole hole, V4Strategy strategy)
        {
            if (remaining[typeIndex] <= 0 || hole == null)
            {
                return null;
            }

            ShapePolygon source = input.PartTypes[typeIndex].Polygon;
            ShapeBounds single = MinimumBounds(source);
            double holeWidth = Math.Max(0.0, hole.MaxX - hole.MinX);
            double holeHeight = Math.Max(0.0, hole.MaxY - hole.MinY);
            int columns = Math.Max(1, (int)Math.Floor((holeWidth + Gap) / Math.Max(1.0, single.Width + Gap)));
            int rows = Math.Max(1, (int)Math.Floor((holeHeight + Gap) / Math.Max(1.0, single.Height + Gap)));
            int maxHorizontal = Math.Min(remaining[typeIndex], Math.Min(MaxComboBlockSize, columns));
            int maxVertical = Math.Min(remaining[typeIndex], Math.Min(MaxComboBlockSize, rows));

            List<V4Template> templates = new List<V4Template>();
            AddRoundTemplate(templates, source, RoundPreferredCount(remaining[typeIndex], maxHorizontal), true, strategy);
            AddRoundTemplate(templates, source, Math.Min(maxHorizontal, 2), true, strategy);
            AddRoundTemplate(templates, source, 1, true, strategy);
            AddRoundTemplate(templates, source, RoundPreferredCount(remaining[typeIndex], maxVertical), false, strategy);
            AddRoundTemplate(templates, source, Math.Min(maxVertical, 2), false, strategy);

            ShapePlacement best = null;
            double bestScore = double.PositiveInfinity;
            foreach (V4Template template in templates)
            {
                foreach (V4Template placementTemplate in PlacementTemplateVariants(template))
                {
                    if (!TemplateFitsHoleDirect(placementTemplate, hole))
                    {
                        continue;
                    }
                    ShapePartInstance part = CreateGroupPart(input, typeIndex, placementTemplate, nextNumber[typeIndex]);
                    foreach (ShapePoint point in RoundHoleCandidatePoints(input, board, placementTemplate, hole))
                    {
                        PumpUi(false);
                        ShapePlacement seed = MakePlacement(part, placementTemplate, boardIndex, point.X, point.Y);
                        ShapePlacement strict = SettlePlacementInsideHole(input, board, seed, placementTemplate, hole);
                        KeepBestRoundHolePlacement(input, board, strict, placementTemplate, hole, 0.0, ref best, ref bestScore);
                        ShapePlacement relaxed = SettlePlacementBottomLeft(input, board, seed, placementTemplate);
                        KeepBestRoundHolePlacement(input, board, relaxed, placementTemplate, hole, input.BoardWidth * 40.0, ref best, ref bestScore);
                    }
                }
            }
            return PolishSelectedHolePlacement(input, board, best, hole);
        }

        private static void KeepBestRoundHolePlacement(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement placement, V4Template template, V4Hole hole, double extraPenalty, ref ShapePlacement best, ref double bestScore)
        {
            if (placement == null || !FitsBoard(input, placement.Bounds) || HitsAny(placement, board))
            {
                return;
            }
            if (!PlacementTouchesHole(placement, hole))
            {
                return;
            }
            double score = RoundHoleFillScore(input, board, placement, hole, 0) + extraPenalty;
            if (score < bestScore)
            {
                bestScore = score;
                best = placement;
            }
        }

        private static List<ShapePoint> RoundHoleCandidatePoints(ShapeNestingInput input, List<ShapePlacement> board, V4Template template, V4Hole hole)
        {
            List<ShapePoint> points = new List<ShapePoint>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            AddCandidate(points, seen, hole.MinX, hole.MinY);
            AddCandidate(points, seen, Math.Max(0.0, hole.MaxX - template.Bounds.Width), hole.MinY);
            AddCandidate(points, seen, hole.MinX, Math.Max(0.0, hole.MaxY - template.Bounds.Height));
            AddCandidate(points, seen, Math.Max(0.0, hole.MaxX - template.Bounds.Width), Math.Max(0.0, hole.MaxY - template.Bounds.Height));
            foreach (ShapePlacement existing in board)
            {
                if (existing.Bounds.MaxX < hole.MinX - template.Bounds.Width || existing.Bounds.MinX > hole.MaxX + template.Bounds.Width ||
                    existing.Bounds.MaxY < hole.MinY - template.Bounds.Height || existing.Bounds.MinY > hole.MaxY + template.Bounds.Height)
                {
                    continue;
                }
                AddCandidate(points, seen, existing.Bounds.MinX, existing.Bounds.MaxY + Gap);
                AddCandidate(points, seen, existing.Bounds.MaxX + Gap, existing.Bounds.MinY);
                AddCandidate(points, seen, Math.Max(0.0, existing.Bounds.MaxX - template.Bounds.Width), existing.Bounds.MinY);
            }
            return CoarseFilterCandidatePoints(input, board, template, LimitPoints(points, FillCandidateLimit), true);
        }

        private static double RoundHoleFillScore(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement placement, V4Hole hole, int rank)
        {
            double overlapX = Math.Max(0.0, Math.Min(placement.Bounds.MaxX, hole.MaxX) - Math.Max(placement.Bounds.MinX, hole.MinX));
            double overlapY = Math.Max(0.0, Math.Min(placement.Bounds.MaxY, hole.MaxY) - Math.Max(placement.Bounds.MinY, hole.MinY));
            double overlapArea = overlapX * overlapY;
            double templateArea = Math.Max(1.0, placement.Bounds.Width * placement.Bounds.Height);
            double contact = FastBoundsContactReward(board, placement.Bounds);
            double usedX = UsedX(board);
            double addedX = Math.Max(0.0, placement.Bounds.MaxX - usedX);
            return placement.Bounds.MinY * 1200000.0 +
                placement.Bounds.MinX * 280000.0 +
                addedX * 500000.0 +
                rank * 500000.0 -
                overlapArea / templateArea * input.BoardWidth * input.BoardHeight * 900.0 -
                CountParts(placement) * input.BoardWidth * input.BoardHeight * 120.0 -
                contact * 900000.0;
        }

        private static List<int> FillCandidateCounts(ShapeNestingInput input, V4Hole hole, int typeIndex, int remaining, V4Strategy strategy)
        {
            if (remaining <= 0)
            {
                return new List<int>();
            }
            int maxCount = EstimateHoleCount(input, hole, typeIndex, remaining, strategy);
            return PreferredCountsFromMax(remaining, maxCount, PreferPairUnits(remaining));
        }

        private static int EstimateHoleCount(ShapeNestingInput input, V4Hole hole, int typeIndex, int remaining, V4Strategy strategy)
        {
            if (remaining <= 0 || hole == null)
            {
                return 0;
            }

            double regionWidth = Math.Max(0.0, hole.MaxX - hole.MinX);
            double regionHeight = Math.Max(0.0, hole.MaxY - hole.MinY);
            ShapePolygon source = input.PartTypes[typeIndex].Polygon;
            double partArea = Math.Max(1.0, Math.Abs(source.Area()));
            int byArea = Math.Max(1, (int)Math.Floor(regionWidth * regionHeight / partArea));
            ShapeBounds single = MinimumBounds(source);
            int columns = Math.Max(1, (int)Math.Floor((regionWidth + Gap) / Math.Max(1.0, single.Width + Gap)));
            int rows = Math.Max(1, (int)Math.Floor((regionHeight + Gap) / Math.Max(1.0, single.Height + Gap)));
            int byGrid = Math.Max(1, columns * rows);
            int estimate = Math.Max(1, Math.Min(byArea, Math.Max(byGrid, strategy.GroupSize)));
            return Math.Min(remaining, Math.Min(MaxComboBlockSize, estimate));
        }

        private static bool TemplateFitsHole(V4Template template, V4Hole hole)
        {
            foreach (V4Template variant in PlacementTemplateVariants(template))
            {
                if (TemplateFitsHoleDirect(variant, hole))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool TemplateFitsHoleDirect(V4Template template, V4Hole hole)
        {
            double holeWidth = Math.Max(0.0, hole.MaxX - hole.MinX);
            double holeHeight = Math.Max(0.0, hole.MaxY - hole.MinY);
            if (hole.Internal)
            {
                return template.Bounds.Width <= holeWidth + 1e-6 && template.Bounds.Height <= holeHeight + 1e-6;
            }
            return template.Bounds.Width <= holeWidth + Gap * 2.0 && template.Bounds.Height <= holeHeight + Gap * 2.0;
        }

        private static List<V4Template> PlacementTemplateVariants(V4Template template)
        {
            List<V4Template> variants = new List<V4Template>();
            AddTemplate(variants, PlacementRotatedTemplate(template, 0));
            AddTemplate(variants, PlacementRotatedTemplate(template, 90));
            return variants;
        }

        private static V4Template PlacementRotatedTemplate(V4Template template, int rotation)
        {
            if (template == null)
            {
                return null;
            }
            if (NormalizeRotation(rotation) == 0)
            {
                return template;
            }

            V4Template rotated = RotateTemplate(template, rotation);
            rotated.PairRotation = template.PairRotation >= 0 ? NormalizeRotation(template.PairRotation + rotation) : -1;
            rotated.ShelfLayout = template.ShelfLayout;
            return rotated;
        }

        private static ShapePlacement BestPlacementOnBoard(ShapeNestingInput input, List<ShapePlacement> board, ShapePartInstance part, V4Template template, int boardIndex, bool fillMode, V4Strategy strategy)
        {
            ShapePlacement best = null;
            double bestScore = double.PositiveInfinity;
            foreach (ShapePoint candidate in CandidatePoints(input, board, template, fillMode))
            {
                PumpUi(false);
                ShapePlacement placement = MakePlacement(part, template, boardIndex, candidate.X, candidate.Y);
                placement = SettlePlacementBottomLeft(input, board, placement, template);
                if (!FitsBoard(input, placement.Bounds) || HitsAny(placement, board))
                {
                    continue;
                }
                double score = fillMode ? FillScore(input, board, placement, template, null, 0) : MainScore(input, board, placement, template, strategy);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = placement;
                }
            }
            return best;
        }

        private static ShapePlacement BestPlacementNearHole(ShapeNestingInput input, List<ShapePlacement> board, ShapePartInstance part, V4Template template, int boardIndex, V4Hole hole, V4Strategy strategy)
        {
            ShapePlacement best = null;
            double bestScore = double.PositiveInfinity;
            foreach (ShapePoint candidate in HoleCandidatePoints(input, board, template, hole))
            {
                PumpUi(false);
                ShapePlacement seed = MakePlacement(part, template, boardIndex, candidate.X, candidate.Y);
                ShapePlacement strict = SettlePlacementInsideHole(input, board, seed, template, hole);
                KeepBestHolePlacement(input, board, strict, template, hole, 0, 0.0, ref best, ref bestScore);
                if (hole.Internal)
                {
                    ShapePlacement relaxed = SettlePlacementBottomLeft(input, board, seed, template);
                    KeepBestHolePlacement(input, board, relaxed, template, hole, 0, input.BoardWidth * 120.0, ref best, ref bestScore);
                }
            }
            return PolishSelectedHolePlacement(input, board, best, hole);
        }

        private static void KeepBestHolePlacement(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement placement, V4Template template, V4Hole hole, int rank, double extraPenalty, ref ShapePlacement best, ref double bestScore)
        {
            if (placement == null || !FitsBoard(input, placement.Bounds) || HitsAny(placement, board))
            {
                return;
            }
            if (!PlacementTouchesHole(placement, hole))
            {
                return;
            }
            double score = FillScore(input, board, placement, template, hole, rank) + extraPenalty + HoleEscapePenalty(placement, hole);
            if (score < bestScore)
            {
                bestScore = score;
                best = placement;
            }
        }

        private static bool PlacementTouchesHole(ShapePlacement placement, V4Hole hole)
        {
            if (hole == null)
            {
                return true;
            }
            double overlapX = Math.Min(placement.Bounds.MaxX, hole.MaxX) - Math.Max(placement.Bounds.MinX, hole.MinX);
            double overlapY = Math.Min(placement.Bounds.MaxY, hole.MaxY) - Math.Max(placement.Bounds.MinY, hole.MinY);
            return overlapX > 1.0 && overlapY > 1.0;
        }

        private static double HoleEscapePenalty(ShapePlacement placement, V4Hole hole)
        {
            if (hole == null || !hole.Internal)
            {
                return 0.0;
            }
            double left = Math.Max(0.0, hole.MinX - placement.Bounds.MinX);
            double right = Math.Max(0.0, placement.Bounds.MaxX - hole.MaxX);
            double above = Math.Max(0.0, placement.Bounds.MaxY - hole.MaxY);
            double below = Math.Max(0.0, hole.MinY - placement.Bounds.MinY);
            return (left + right) * 90000.0 + above * 120000.0 + below * 14000.0;
        }

        private static ShapePlacement SettlePlacementInsideHole(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement placement, V4Template template, V4Hole hole)
        {
            if (SelectiveSlidePolishExperiment)
            {
                return placement;
            }

            return FullSettlePlacementInsideHole(input, board, placement, template, hole);
        }

        private static ShapePlacement FullSettlePlacementInsideHole(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement placement, V4Template template, V4Hole hole)
        {
            ShapePlacement current = placement;
            for (int i = 0; i < 10; i++)
            {
                double oldX = current.X;
                double oldY = current.Y;
                current = SlidePlacementInsideHole(input, board, current, template, hole, 0.0, -1.0);
                current = SlidePlacementInsideHole(input, board, current, template, hole, -1.0, 0.0);
                current = SlidePlacementInsideHole(input, board, current, template, hole, 0.0, -1.0);
                if (Math.Abs(current.X - oldX) < 0.01 && Math.Abs(current.Y - oldY) < 0.01)
                {
                    break;
                }
            }
            return current;
        }

        private static ShapePlacement SlidePlacementInsideHole(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement placement, V4Template template, V4Hole hole, double dx, double dy)
        {
            double slackX = hole.Internal ? 0.0 : template.Bounds.Width * 0.35;
            double slackY = hole.Internal ? 0.0 : template.Bounds.Height * 0.35;
            double minX = Math.Max(0.0, hole.MinX - slackX);
            double maxX = Math.Min(input.BoardWidth - template.Bounds.Width, hole.Internal ? hole.MaxX - template.Bounds.Width : hole.MaxX);
            double minY = Math.Max(0.0, hole.MinY - slackY);
            double maxY = Math.Min(input.BoardHeight - template.Bounds.Height, hole.Internal ? hole.MaxY - template.Bounds.Height : hole.MaxY);
            if (maxX < minX - 1e-6 || maxY < minY - 1e-6)
            {
                return placement;
            }
            if ((dx < 0.0 && placement.X <= minX + 1e-6) || (dy < 0.0 && placement.Y <= minY + 1e-6))
            {
                return placement;
            }

            double maxTravel = dx < 0.0 ? placement.X - minX : placement.Y - minY;
            if (maxTravel <= 1e-6)
            {
                return placement;
            }

            double low = 0.0;
            double high = maxTravel;
            for (int i = 0; i < 16; i++)
            {
                PumpUi(false);
                double mid = (low + high) / 2.0;
                double x = placement.X + dx * mid;
                double y = placement.Y + dy * mid;
                if (x < minX - 1e-6 || x > maxX + 1e-6 || y < minY - 1e-6 || y > maxY + 1e-6)
                {
                    high = mid;
                    continue;
                }
                ShapePlacement candidate = MakePlacement(placement.Part, template, placement.BoardIndex, x, y);
                if (FitsBoard(input, candidate.Bounds) && !HitsAny(candidate, board))
                {
                    low = mid;
                }
                else
                {
                    high = mid;
                }
            }
            return MakePlacement(placement.Part, template, placement.BoardIndex, placement.X + dx * low, placement.Y + dy * low);
        }

        private static List<V4Template> BuildTemplatesForType(ShapeNestingInput input, int typeIndex, int remaining, V4Strategy strategy, Dictionary<string, List<V4Template>> templateCache)
        {
            int count = Math.Min(remaining, strategy.GroupSize);
            return BuildTemplatesForCount(input, typeIndex, count, strategy, templateCache);
        }

        private static List<V4Template> BuildTemplatesForCountFast(ShapeNestingInput input, int typeIndex, int count, V4Strategy strategy, Dictionary<string, List<V4Template>> templateCache, bool fastPairCounts)
        {
            if (fastPairCounts && count > 1 && count % 2 != 0)
            {
                return new List<V4Template>();
            }
            if (!fastPairCounts || count <= 2)
            {
                return BuildTemplatesForCount(input, typeIndex, count, strategy, templateCache);
            }

            string cacheKey = "fast:" +
                typeIndex.ToString(CultureInfo.InvariantCulture) + ":" +
                count.ToString(CultureInfo.InvariantCulture) + ":" +
                strategy.MainRotation.ToString(CultureInfo.InvariantCulture) + ":" +
                strategy.ExpansionMode.ToString(CultureInfo.InvariantCulture);
            List<V4Template> cached;
            if (templateCache != null && templateCache.TryGetValue(cacheKey, out cached))
            {
                return cached;
            }

            List<V4Template> templates = new List<V4Template>();
            ShapePolygon source = input.PartTypes[typeIndex].Polygon;
            foreach (V4Template pair in BuildPairHugTemplates(input, source, strategy, -1, true))
            {
                PumpUi(false);
                AddPairRegularTemplates(input, templates, pair, count, strategy);
                break;
            }
            templates.Sort(delegate (V4Template a, V4Template b)
            {
                return ExplicitPairTemplateOrder(a).CompareTo(ExplicitPairTemplateOrder(b));
            });
            AddHugGalleryTemplates(input, typeIndex, templates, "hug gallery " + input.PartTypes[typeIndex].Name + " x" + count.ToString(CultureInfo.InvariantCulture));
            if (templateCache != null)
            {
                templateCache[cacheKey] = templates;
            }
            return templates;
        }

        private static int ExplicitPairTemplateOrder(V4Template template)
        {
            if (template == null)
            {
                return int.MaxValue;
            }
            int rotation = NormalizeRotation(template.PairRotation);
            bool horizontal = template.Bounds.Width >= template.Bounds.Height;
            return (rotation == 90 ? 10 : 0) + (horizontal ? 0 : 1);
        }

        private static List<V4Template> BuildTemplatesForCount(ShapeNestingInput input, int typeIndex, int count, V4Strategy strategy, Dictionary<string, List<V4Template>> templateCache)
        {
            string cacheKey = typeIndex.ToString(CultureInfo.InvariantCulture) + ":" +
                count.ToString(CultureInfo.InvariantCulture) + ":" +
                strategy.MainRotation.ToString(CultureInfo.InvariantCulture) + ":" +
                Math.Min(strategy.GroupSize, MaxComboBlockSize).ToString(CultureInfo.InvariantCulture) + ":" +
                strategy.ExpansionMode.ToString(CultureInfo.InvariantCulture);
            List<V4Template> cached;
            if (templateCache != null && templateCache.TryGetValue(cacheKey, out cached))
            {
                return cached;
            }

            List<V4Template> templates = new List<V4Template>();
            ShapePolygon source = input.PartTypes[typeIndex].Polygon;
            double partArea = Math.Max(1.0, source.Area());
            if (count <= 1)
            {
                AddTemplateOrientations(templates, SingleTemplate(source, strategy.MainRotation), false);
            }
            else
            {
                List<V4Template> pairs = BuildPairHugTemplates(input, source, strategy, count == 2 ? typeIndex : -1, true);
                foreach (V4Template pair in pairs)
                {
                    if (count == 2)
                    {
                        AddPairUnitOrientations(templates, pair);
                    }
                    else
                    {
                        AddComboTemplatesFromPair(input, templates, pair, source, count, strategy, partArea);
                    }
                }
            }
            if (templates.Count == 0)
            {
                AddTemplate(templates, TightHugTemplate(input, source, count, strategy));
            }
            if (templates.Count == 0)
            {
                AddTemplate(templates, SingleTemplate(source, strategy.MainRotation));
            }
            if (count > 1 && count % 2 == 0)
            {
                templates.Sort(delegate (V4Template a, V4Template b)
                {
                    return ExplicitPairTemplateOrder(a).CompareTo(ExplicitPairTemplateOrder(b));
                });
            }
            else
            {
                templates = count > 2 ? DiverseRegularComboTemplates(templates, partArea, strategy, TemplateVariantLimit) : BestRatioTemplates(templates, partArea);
            }
            if (count <= 2)
            {
                templates.Sort(delegate (V4Template a, V4Template b)
                {
                    return TemplateScore(a, strategy).CompareTo(TemplateScore(b, strategy));
                });
            }
            AddHugGalleryTemplates(input, typeIndex, templates, "hug gallery " + input.PartTypes[typeIndex].Name + " x" + count.ToString(CultureInfo.InvariantCulture));
            if (templateCache != null)
            {
                templateCache[cacheKey] = templates;
            }
            return templates;
        }

        private static void AddTemplateOrientations(List<V4Template> templates, V4Template template, bool shelfLayout)
        {
            if (template == null)
            {
                return;
            }
            foreach (double rotation in TemplatePlacementAngles(template.Polygons))
            {
                V4Template rotated = RotateTemplate(template, rotation);
                rotated.PairRotation = template.Count > 1 && IsAxisQuarterTurn(rotation) ? NormalizeRotation((int)Math.Round(rotation)) : -1;
                rotated.ShelfLayout = shelfLayout || template.ShelfLayout;
                AddTemplate(templates, rotated);
            }
        }

        private static void AddPairUnitOrientations(List<V4Template> templates, V4Template pair)
        {
            if (pair == null)
            {
                return;
            }
            AddTemplateAllowSameShape(templates, PairUsageTemplate(pair, 0));
            AddTemplateAllowSameShape(templates, PairUsageTemplate(pair, 90));
        }

        private static V4Template PairUsageTemplate(V4Template pair, int rotation)
        {
            V4Template template = RotateTemplate(pair, rotation);
            template.PairRotation = NormalizeRotation(rotation);
            return template;
        }

        private static void AddComboTemplatesFromPair(ShapeNestingInput input, List<V4Template> templates, V4Template pair, ShapePolygon source, int count, V4Strategy strategy, double partArea)
        {
            AddPairRegularTemplates(input, templates, pair, count, strategy);
        }

        private static void AddPairRegularTemplates(ShapeNestingInput input, List<V4Template> templates, V4Template pair, int count, V4Strategy strategy)
        {
            if (pair == null || count < 2)
            {
                return;
            }

            foreach (V4Template template in BuildPairStackTemplates(input, pair, count, strategy))
            {
                AddTemplateAllowSameShape(templates, template);
            }
        }

        private static List<V4Template> BuildPairStackTemplates(ShapeNestingInput input, V4Template pair, int count, V4Strategy strategy)
        {
            List<V4Template> result = new List<V4Template>();
            if (pair == null || count < 2 || count % 2 != 0)
            {
                return result;
            }

            AddPairLineTemplates(input, result, pair, count);
            return result;
        }

        private static void AddPairLineTemplates(ShapeNestingInput input, List<V4Template> result, V4Template pair, int count)
        {
            foreach (int unitRotation in new int[] { 0, 90 })
            {
                foreach (bool horizontal in new bool[] { true, false })
                {
                    V4Template template = BuildPairLineTemplate(input, pair, count, unitRotation, horizontal);
                    if (template != null)
                    {
                        AddTemplateAllowSameShape(result, template);
                    }
                }
            }
        }

        private static V4Template BuildPairLineTemplate(ShapeNestingInput input, V4Template pair, int count, int unitRotation, bool horizontal)
        {
            if (pair == null || count < 2 || count % 2 != 0)
            {
                return null;
            }

            int unitCount = count / 2;
            List<ShapePolygon> unit = RotateAndNormalize(pair.Polygons, unitRotation);
            List<ShapePolygon> polygons = new List<ShapePolygon>(unit);
            for (int i = 1; i < unitCount; i++)
            {
                PumpUi(false);
                List<ShapePolygon> moved;
                if (!AppendPairUnitOnLine(input, polygons, unit, horizontal, out moved))
                {
                    return null;
                }
                polygons.AddRange(moved);
                polygons = NormalizePolygons(polygons);
            }

            V4Template template = DropTemplateBottomLeft(input, NormalizeTemplate(polygons));
            if (!TemplateCanFitBoardInUsefulOrientation(input, template.Polygons) || !IsCompactHugCandidate(template))
            {
                return null;
            }
            template.PairRotation = NormalizeRotation(unitRotation);
            return template;
        }

        private static bool AppendPairUnitOnLine(ShapeNestingInput input, List<ShapePolygon> placed, List<ShapePolygon> unit, bool horizontal, out List<ShapePolygon> moved)
        {
            moved = null;
            List<ShapePolygon> normalized = NormalizePolygons(unit);
            ShapeBounds placedBounds = BoundsOf(placed);
            ShapeBounds movingBounds = BoundsOf(normalized);
            double axisLimit = Math.Max(input.BoardWidth, input.BoardHeight);
            double high = horizontal ? placedBounds.MaxX - movingBounds.MinX : placedBounds.MaxY - movingBounds.MinY;
            high = Math.Max(0.0, high);
            double step = Math.Max(1.0, horizontal ? movingBounds.Width / 3.0 : movingBounds.Height / 3.0);
            int guard = 0;
            while (guard < 80 && GroupPolygonsOverlap(MoveUnitOnLine(normalized, high, horizontal), placed))
            {
                PumpUi(false);
                high += step;
                if (high > axisLimit + step)
                {
                    return false;
                }
                guard++;
            }

            double low = 0.0;
            if (!GroupPolygonsOverlap(MoveUnitOnLine(normalized, low, horizontal), placed))
            {
                high = low;
            }
            else
            {
                for (int i = 0; i < 24; i++)
                {
                    PumpUi(false);
                    double mid = (low + high) / 2.0;
                    if (GroupPolygonsOverlap(MoveUnitOnLine(normalized, mid, horizontal), placed))
                    {
                        low = mid;
                    }
                    else
                    {
                        high = mid;
                    }
                }
            }

            high += Math.Max(0.02, PairHugGap * 0.1);
            moved = MoveUnitOnLine(normalized, high, horizontal);
            List<ShapePolygon> all = new List<ShapePolygon>(placed);
            all.AddRange(moved);
            if (!TemplateCanFitBoardInUsefulOrientation(input, all) || GroupPolygonsOverlap(moved, placed))
            {
                return false;
            }
            return true;
        }

        private static List<ShapePolygon> MoveUnitOnLine(List<ShapePolygon> unit, double offset, bool horizontal)
        {
            return TranslatePolygons(unit, horizontal ? offset : 0.0, horizontal ? 0.0 : offset);
        }

        private static List<List<int>> PairStackRotationSequences(int unitCount)
        {
            List<List<int>> sequences = new List<List<int>>();
            if (unitCount <= 0)
            {
                return sequences;
            }

            int maxHorizontal = unitCount;
            int minHorizontal = (unitCount + 1) / 2;
            for (int horizontal = maxHorizontal; horizontal >= minHorizontal; horizontal--)
            {
                List<int> sequence = new List<int>();
                for (int i = 0; i < horizontal; i++)
                {
                    sequence.Add(0);
                }
                for (int i = horizontal; i < unitCount; i++)
                {
                    sequence.Add(90);
                }
                sequences.Add(sequence);
            }
            return sequences;
        }

        private static List<List<int>> UniqueRotationOrders(List<int> rotations)
        {
            List<List<int>> orders = new List<List<int>>();
            bool[] used = new bool[rotations.Count];
            BuildRotationOrders(rotations, used, new List<int>(), orders);
            return orders;
        }

        private static void BuildRotationOrders(List<int> rotations, bool[] used, List<int> current, List<List<int>> orders)
        {
            if (current.Count == rotations.Count)
            {
                orders.Add(new List<int>(current));
                return;
            }

            Dictionary<int, bool> usedAtDepth = new Dictionary<int, bool>();
            for (int i = 0; i < rotations.Count; i++)
            {
                if (used[i])
                {
                    continue;
                }
                int rotation = NormalizeRotation(rotations[i]);
                if (usedAtDepth.ContainsKey(rotation))
                {
                    continue;
                }
                usedAtDepth[rotation] = true;
                used[i] = true;
                current.Add(rotation);
                BuildRotationOrders(rotations, used, current, orders);
                current.RemoveAt(current.Count - 1);
                used[i] = false;
            }
        }

        private static V4Template BuildPairStackTemplateForOrder(ShapeNestingInput input, V4Template pair, List<int> order, V4Strategy strategy)
        {
            if (order == null || order.Count == 0)
            {
                return null;
            }

            List<V4Template> states = new List<V4Template>();
            AddTemplate(states, NormalizeTemplate(RotateAndNormalize(pair.Polygons, order[0])));
            for (int i = 1; i < order.Count; i++)
            {
                List<V4Template> candidates = new List<V4Template>();
                List<ShapePolygon> unit = RotateAndNormalize(pair.Polygons, order[i]);
                foreach (V4Template state in states)
                {
                    AddStackUnitCandidates(input, candidates, state.Polygons, unit, order[i]);
                }
                if (candidates.Count == 0)
                {
                    return null;
                }
                states = LimitStackStates(candidates, strategy, Math.Max(4, TemplateVariantLimit / 2));
            }

            if (states.Count == 0)
            {
                return null;
            }
            states.Sort(delegate (V4Template a, V4Template b)
            {
                return HugSuccessScore(a, strategy).CompareTo(HugSuccessScore(b, strategy));
            });

            V4Template template = DropTemplateBottomLeft(input, states[0]);
            if (!TemplateCanFitBoardInUsefulOrientation(input, template.Polygons) || !IsCompactHugCandidate(template))
            {
                return null;
            }
            template.PairRotation = NormalizeRotation(order[0]);
            return template;
        }

        private static V4Template DropTemplateBottomLeft(ShapeNestingInput input, V4Template template)
        {
            if (template == null)
            {
                return null;
            }
            List<ShapePolygon> current = NormalizePolygons(template.Polygons);
            List<ShapePolygon> empty = new List<ShapePolygon>();
            for (int i = 0; i < 8; i++)
            {
                ShapeBounds before = BoundsOf(current);
                current = SlideMovingGroup(input, empty, current, 0.0, -1.0);
                current = SlideMovingGroup(input, empty, current, -1.0, 0.0);
                current = SlideMovingGroup(input, empty, current, 0.0, -1.0);
                current = NormalizePolygons(current);
                ShapeBounds after = BoundsOf(current);
                if (Math.Abs(after.MinX - before.MinX) < 0.01 && Math.Abs(after.MinY - before.MinY) < 0.01)
                {
                    break;
                }
            }
            V4Template dropped = NormalizeTemplate(current);
            dropped.PairRotation = template.PairRotation;
            dropped.ShelfLayout = template.ShelfLayout;
            return dropped;
        }

        private static void AddStackUnitCandidates(ShapeNestingInput input, List<V4Template> candidates, List<ShapePolygon> placed, List<ShapePolygon> unit, int unitRotation)
        {
            List<ShapePolygon> normalized = NormalizePolygons(unit);
            foreach (ShapePoint candidate in StackCandidateOffsets(placed, normalized))
            {
                PumpUi(false);
                List<ShapePolygon> moved = TranslatePolygons(normalized, candidate.X, candidate.Y);
                moved = SettleMovingGroupBottomLeft(input, placed, moved);
                if (GroupPolygonsOverlap(moved, placed))
                {
                    continue;
                }
                if (!GroupsHugAfterSettle(moved, placed))
                {
                    continue;
                }

                List<ShapePolygon> all = new List<ShapePolygon>(placed);
                all.AddRange(moved);
                V4Template template = NormalizeTemplate(all);
                if (!TemplateCanFitBoardInUsefulOrientation(input, template.Polygons))
                {
                    continue;
                }
                if (!IsCompactHugCandidate(template))
                {
                    continue;
                }
                template.PairRotation = NormalizeRotation(unitRotation);
                AddTemplate(candidates, template);
            }
        }

        private static List<ShapePoint> StackCandidateOffsets(List<ShapePolygon> placed, List<ShapePolygon> moving)
        {
            List<ShapePoint> nfpCandidates = NfpGroupCandidateOffsets(placed, moving, GroupCandidateLimit);
            if (nfpCandidates.Count > 0)
            {
                return nfpCandidates;
            }

            List<ShapePoint> candidates = new List<ShapePoint>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            ShapeBounds placedBounds = BoundsOf(placed);
            ShapeBounds movingBounds = BoundsOf(moving);

            AddStackOffsetsAroundBounds(candidates, seen, placedBounds, movingBounds);
            foreach (ShapeBounds unitBounds in TemplateUnitBounds(NormalizeTemplate(placed)))
            {
                AddStackOffsetsAroundBounds(candidates, seen, unitBounds, movingBounds);
                if (candidates.Count >= GroupCandidateLimit)
                {
                    break;
                }
            }
            return candidates;
        }

        private static void AddStackOffsetsAroundBounds(List<ShapePoint> candidates, Dictionary<string, bool> seen, ShapeBounds fixedBounds, ShapeBounds movingBounds)
        {
            double rightX = fixedBounds.MaxX + PairHugGap - movingBounds.MinX;
            double leftX = fixedBounds.MinX - PairHugGap - movingBounds.MaxX;
            double topY = fixedBounds.MaxY + PairHugGap - movingBounds.MinY;
            double bottomY = fixedBounds.MinY - PairHugGap - movingBounds.MaxY;

            AddOffset(candidates, seen, rightX, fixedBounds.MinY - movingBounds.MinY);
            AddOffset(candidates, seen, rightX, fixedBounds.MaxY - movingBounds.MaxY);
            AddOffset(candidates, seen, rightX, (fixedBounds.MinY + fixedBounds.MaxY - movingBounds.MinY - movingBounds.MaxY) / 2.0);

            AddOffset(candidates, seen, leftX, fixedBounds.MinY - movingBounds.MinY);
            AddOffset(candidates, seen, leftX, fixedBounds.MaxY - movingBounds.MaxY);
            AddOffset(candidates, seen, leftX, (fixedBounds.MinY + fixedBounds.MaxY - movingBounds.MinY - movingBounds.MaxY) / 2.0);

            AddOffset(candidates, seen, fixedBounds.MinX - movingBounds.MinX, topY);
            AddOffset(candidates, seen, fixedBounds.MaxX - movingBounds.MaxX, topY);
            AddOffset(candidates, seen, (fixedBounds.MinX + fixedBounds.MaxX - movingBounds.MinX - movingBounds.MaxX) / 2.0, topY);

            AddOffset(candidates, seen, fixedBounds.MinX - movingBounds.MinX, bottomY);
            AddOffset(candidates, seen, fixedBounds.MaxX - movingBounds.MaxX, bottomY);
            AddOffset(candidates, seen, (fixedBounds.MinX + fixedBounds.MaxX - movingBounds.MinX - movingBounds.MaxX) / 2.0, bottomY);
        }

        private static List<V4Template> LimitStackStates(List<V4Template> templates, V4Strategy strategy, int limit)
        {
            return SelectDimensionDiverseTemplates(templates, strategy, limit);
        }

        private static bool GroupsHugAfterSettle(List<ShapePolygon> moved, List<ShapePolygon> placed)
        {
            double best = double.PositiveInfinity;
            foreach (ShapePolygon a in moved)
            {
                foreach (ShapePolygon b in placed)
                {
                    best = Math.Min(best, MinEdgeDistance(a, b));
                    if (best <= Math.Max(6.0, Gap * 3.0))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool IsCompactHugCandidate(V4Template template)
        {
            if (template == null || template.Count <= 2)
            {
                return true;
            }

            return TemplateFilledRatio(template) >= 0.42;
        }

        private static List<int> PairGridColumns(int count)
        {
            List<int> columns = new List<int>();
            int unitCount = count / 2;
            if (unitCount <= 0 || count % 2 != 0)
            {
                return columns;
            }

            for (int value = 1; value <= unitCount; value++)
            {
                if (unitCount % value == 0)
                {
                    columns.Add(value);
                }
            }
            return columns;
        }

        private static V4Template PairRegularGridTemplate(ShapeNestingInput input, V4Template pair, int count, int pairRotation, int columns, int pattern)
        {
            if (pair == null || count < 2 || count % 2 != 0)
            {
                return null;
            }

            int unitCount = count / 2;
            if (columns <= 0 || unitCount % columns != 0)
            {
                return null;
            }

            int rows = unitCount / columns;
            List<ShapePolygon> polygons = null;
            for (int row = 0; row < rows; row++)
            {
                PumpUi(false);
                List<ShapePolygon> rowPolygons = PairRegularGridRow(input, pair, columns, pairRotation, pattern, row);
                if (rowPolygons == null || rowPolygons.Count == 0)
                {
                    return null;
                }

                if (polygons == null)
                {
                    polygons = new List<ShapePolygon>(rowPolygons);
                    continue;
                }

                List<ShapePolygon> moved;
                if (!BestStackedGroupAddition(input, polygons, new List<List<ShapePolygon>> { rowPolygons }, 2, out moved))
                {
                    return null;
                }
                polygons.AddRange(moved);
                polygons = NormalizePolygons(polygons);
            }

            if (polygons == null || polygons.Count != count)
            {
                return null;
            }

            V4Template template = NormalizeTemplate(polygons);
            if (!TemplateCanFitBoardInUsefulOrientation(input, template.Polygons))
            {
                return null;
            }
            template.PairRotation = NormalizeRotation(pairRotation);
            return template;
        }

        private static List<ShapePolygon> PairRegularGridRow(ShapeNestingInput input, V4Template pair, int columns, int pairRotation, int pattern, int row)
        {
            List<ShapePolygon> polygons = null;
            for (int column = 0; column < columns; column++)
            {
                PumpUi(false);
                int rotation = PairRegularGridUnitRotation(pairRotation, pattern, row, column);
                List<ShapePolygon> unit = RotateAndNormalize(pair.Polygons, rotation);
                if (polygons == null)
                {
                    polygons = new List<ShapePolygon>(unit);
                    continue;
                }

                List<ShapePolygon> moved;
                if (!BestStackedGroupAddition(input, polygons, new List<List<ShapePolygon>> { unit }, 1, out moved))
                {
                    return null;
                }
                polygons.AddRange(moved);
                polygons = NormalizePolygons(polygons);
            }
            return polygons == null ? null : NormalizePolygons(polygons);
        }

        private static int PairRegularGridUnitRotation(int pairRotation, int pattern, int row, int column)
        {
            int rotation = NormalizeRotation(pairRotation);
            if ((pattern == 1 && row % 2 == 1) ||
                (pattern == 2 && column % 2 == 1))
            {
                rotation = NormalizeRotation(rotation + 90);
            }
            return rotation;
        }

        private static bool BestStackedGroupAddition(ShapeNestingInput input, List<ShapePolygon> placed, List<List<ShapePolygon>> movingOptions, int mode, out List<ShapePolygon> bestMoved)
        {
            bestMoved = null;
            double bestScore = double.PositiveInfinity;
            List<ShapePolygon> anchors = ChainAnchors(placed, mode);
            foreach (List<ShapePolygon> option in movingOptions)
            {
                PumpUi(false);
                List<ShapePolygon> normalized = NormalizePolygons(option);
                List<ShapePoint> candidates = GroupCandidateOffsets(anchors, normalized);
                foreach (ShapePoint candidate in candidates)
                {
                    List<ShapePolygon> moved = TranslatePolygons(normalized, candidate.X, candidate.Y);
                    moved = SettleMovingGroupBottomLeft(input, placed, moved);
                    if (GroupPolygonsOverlap(moved, placed))
                    {
                        continue;
                    }
                    List<ShapePolygon> all = new List<ShapePolygon>(placed);
                    all.AddRange(moved);
                    ShapeBounds bounds = BoundsOf(all);
                    if (bounds.MinX < -1e-6 || bounds.MinY < -1e-6 ||
                        bounds.Width > input.BoardWidth + 1e-6 || bounds.Height > input.BoardHeight + 1e-6)
                    {
                        continue;
                    }
                    double near = GroupNearReward(moved, placed);
                    double contact = GroupContactReward(moved, placed);
                    double score = PackedGroupScore(bounds, near, contact, mode) + StackRegularityPenalty(all);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestMoved = moved;
                    }
                }
            }
            return bestMoved != null;
        }

        private static List<ShapePolygon> SettleMovingGroupBottomLeft(ShapeNestingInput input, List<ShapePolygon> placed, List<ShapePolygon> moving)
        {
            List<ShapePolygon> current = moving;
            List<ShapePolygon> nfp = NfpSettleMovingGroup(input, placed, current);
            if (nfp != null)
            {
                current = nfp;
            }
            for (int i = 0; i < 10; i++)
            {
                ShapeBounds before = BoundsOf(current);
                current = SlideMovingGroup(input, placed, current, 0.0, -1.0);
                current = SlideMovingGroup(input, placed, current, -1.0, 0.0);
                current = SlideMovingGroup(input, placed, current, 0.0, -1.0);
                ShapeBounds after = BoundsOf(current);
                if (Math.Abs(after.MinX - before.MinX) < 0.01 && Math.Abs(after.MinY - before.MinY) < 0.01)
                {
                    break;
                }
            }
            return current;
        }

        private static List<ShapePolygon> SlideMovingGroup(ShapeNestingInput input, List<ShapePolygon> placed, List<ShapePolygon> moving, double dx, double dy)
        {
            ShapeBounds bounds = BoundsOf(moving);
            double maxTravel = 0.0;
            if (dx < 0.0)
            {
                maxTravel = bounds.MinX;
            }
            else if (dx > 0.0)
            {
                maxTravel = input.BoardWidth - bounds.MaxX;
            }
            else if (dy < 0.0)
            {
                maxTravel = bounds.MinY;
            }
            else if (dy > 0.0)
            {
                maxTravel = input.BoardHeight - bounds.MaxY;
            }

            if (maxTravel <= 1e-6)
            {
                return moving;
            }

            double low = 0.0;
            double high = maxTravel;
            for (int i = 0; i < 16; i++)
            {
                PumpUi(false);
                double mid = (low + high) / 2.0;
                List<ShapePolygon> candidate = TranslatePolygons(moving, dx * mid, dy * mid);
                ShapeBounds candidateBounds = BoundsOf(candidate);
                if (candidateBounds.MinX < -1e-6 || candidateBounds.MinY < -1e-6 ||
                    candidateBounds.MaxX > input.BoardWidth + 1e-6 || candidateBounds.MaxY > input.BoardHeight + 1e-6 ||
                    GroupPolygonsOverlap(candidate, placed))
                {
                    high = mid;
                }
                else
                {
                    low = mid;
                }
            }
            return low <= 1e-6 ? moving : TranslatePolygons(moving, dx * low, dy * low);
        }

        private static double StackRegularityPenalty(List<ShapePolygon> polygons)
        {
            ShapeBounds bounds = BoundsOf(polygons);
            double penalty = bounds.MinX * 200.0 + bounds.MinY * 200.0;
            List<double> xs = new List<double>();
            List<double> ys = new List<double>();
            foreach (ShapePolygon polygon in polygons)
            {
                ShapeBounds b = polygon.Bounds();
                xs.Add((b.MinX + b.MaxX) / 2.0);
                ys.Add((b.MinY + b.MaxY) / 2.0);
            }
            penalty += AxisSpreadPenalty(xs, bounds.Width);
            penalty += AxisSpreadPenalty(ys, bounds.Height);
            return penalty;
        }

        private static double AxisSpreadPenalty(List<double> values, double span)
        {
            if (values.Count <= 2 || span <= 1e-6)
            {
                return 0.0;
            }
            values.Sort();
            double gapSum = 0.0;
            double maxGap = 0.0;
            for (int i = 1; i < values.Count; i++)
            {
                double gap = Math.Max(0.0, values[i] - values[i - 1]);
                gapSum += gap;
                maxGap = Math.Max(maxGap, gap);
            }
            double average = gapSum / Math.Max(1, values.Count - 1);
            return Math.Max(0.0, maxGap - average * 1.8) * 120.0;
        }

        private static List<V4Template> BuildComboGrowTemplates(ShapeNestingInput input, ShapePolygon source, List<ShapePolygon> seed, int count, V4Strategy strategy, double partArea)
        {
            List<V4Template> result = new List<V4Template>();
            if (seed == null || seed.Count == 0 || seed.Count > count)
            {
                return result;
            }

            V4Template start = NormalizeTemplate(seed);
            if (InternalTemplateOverlap(start.Polygons) || !TemplateCanFitBoardInUsefulOrientation(input, start.Polygons))
            {
                return result;
            }

            List<V4Template> states = new List<V4Template> { start };
            while (states.Count > 0 && states[0].Count < count)
            {
                PumpUi(false);
                List<V4Template> candidates = new List<V4Template>();
                foreach (V4Template state in states)
                {
                    PumpUi(false);
                    foreach (double rotation in ComboPartRotations(source, strategy, state.Count))
                    {
                        PumpUi(false);
                        ShapePolygon moving = RotatePolygonNormalize(source, rotation);
                        AddComboAdditions(input, candidates, state.Polygons, moving, strategy);
                    }
                }
                if (candidates.Count == 0)
                {
                    break;
                }
                states = LimitComboStates(candidates, partArea, strategy, TemplateVariantLimit * 2);
            }

            foreach (V4Template state in states)
            {
                if (state.Count == count)
                {
                    AddTemplate(result, state);
                }
            }
            return BestRatioTemplates(result, partArea);
        }

        private static List<double> ComboPartRotations(ShapePolygon source, V4Strategy strategy, int step)
        {
            List<double> angles = new List<double>();
            int main = NormalizeRotation(strategy.MainRotation);
            int alternate = NormalizeRotation(main + 90);
            if (strategy.ExpansionMode == 1)
            {
                AddAngles(angles, step % 2 == 0 ? new double[] { main, alternate, 180, 270 } : new double[] { alternate, main, 270, 180 });
            }
            else if (strategy.ExpansionMode == 2)
            {
                AddAngles(angles, step % 2 == 0 ? new double[] { alternate, main, 270, 180 } : new double[] { main, alternate, 180, 270 });
            }
            else
            {
                AddAngles(angles, new double[] { main, alternate, NormalizeRotation(main + 180), NormalizeRotation(alternate + 180) });
            }

            foreach (double angle in MinBoundingBoxAngles(new List<ShapePolygon> { source }))
            {
                AddAngle(angles, angle);
                AddAngle(angles, angle + 180.0);
            }
            return angles;
        }

        private static void AddComboAdditions(ShapeNestingInput input, List<V4Template> candidates, List<ShapePolygon> placed, ShapePolygon moving, V4Strategy strategy)
        {
            List<ShapePolygon> movingGroup = new List<ShapePolygon> { moving };
            foreach (ShapePoint offset in ComboCandidateOffsets(ChainAnchors(placed, strategy.ExpansionMode), movingGroup))
            {
                PumpUi(false);
                List<ShapePolygon> moved = TranslatePolygons(movingGroup, offset.X, offset.Y);
                if (GroupPolygonsOverlap(moved, placed))
                {
                    continue;
                }
                List<ShapePolygon> all = new List<ShapePolygon>(placed);
                all.AddRange(moved);
                V4Template template = NormalizeTemplate(all);
                if (!TemplateCanFitBoardInUsefulOrientation(input, template.Polygons))
                {
                    continue;
                }
                AddTemplate(candidates, template);
            }
        }

        private static List<ShapePoint> ComboCandidateOffsets(List<ShapePolygon> placed, List<ShapePolygon> moving)
        {
            List<ShapePoint> candidates = new List<ShapePoint>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            ShapeBounds mb = BoundsOf(moving);
            List<ShapePoint> movingAnchors = LimitAnchors(TemplateAnchorPoints(moving), 24);
            foreach (ShapePolygon existing in placed)
            {
                PumpUi(false);
                ShapeBounds b = existing.Bounds();
                AddOffset(candidates, seen, b.MaxX + PairHugGap - mb.MinX, b.MinY - mb.MinY);
                AddOffset(candidates, seen, b.MinX - PairHugGap - mb.MaxX, b.MinY - mb.MinY);
                AddOffset(candidates, seen, b.MinX - mb.MinX, b.MaxY + PairHugGap - mb.MinY);
                AddOffset(candidates, seen, b.MinX - mb.MinX, b.MinY - PairHugGap - mb.MaxY);
                AddOffset(candidates, seen, b.MaxX - mb.Width, b.MinY - mb.MinY);
                AddOffset(candidates, seen, b.MinX - mb.MinX, b.MaxY - mb.Height);

                List<ShapePoint> fixedAnchors = LimitAnchors(HugAnchorPoints(existing), 14);
                foreach (ShapePoint fixedPoint in fixedAnchors)
                {
                    PumpUi(false);
                    foreach (ShapePoint movingPoint in movingAnchors)
                    {
                        AddOffset(candidates, seen, fixedPoint.X - movingPoint.X + PairHugGap, fixedPoint.Y - movingPoint.Y);
                        AddOffset(candidates, seen, fixedPoint.X - movingPoint.X - PairHugGap, fixedPoint.Y - movingPoint.Y);
                        AddOffset(candidates, seen, fixedPoint.X - movingPoint.X, fixedPoint.Y - movingPoint.Y + PairHugGap);
                        AddOffset(candidates, seen, fixedPoint.X - movingPoint.X, fixedPoint.Y - movingPoint.Y - PairHugGap);
                        if (candidates.Count >= GroupCandidateLimit)
                        {
                            return candidates;
                        }
                    }
                }
                if (candidates.Count >= GroupCandidateLimit)
                {
                    return candidates;
                }
            }
            return candidates;
        }

        private static List<V4Template> LimitComboStates(List<V4Template> templates, double partArea, V4Strategy strategy, int limit)
        {
            templates.Sort(delegate (V4Template a, V4Template b)
            {
                double ar = TemplateOccupiedRatio(a, partArea);
                double br = TemplateOccupiedRatio(b, partArea);
                int cmp = CompareDescending(ar, br, TemplateRatioTolerance);
                if (cmp != 0)
                {
                    return cmp;
                }
                return TemplateScore(a, strategy).CompareTo(TemplateScore(b, strategy));
            });
            if (templates.Count <= limit)
            {
                return templates;
            }
            templates.RemoveRange(limit, templates.Count - limit);
            return templates;
        }

        private static void AddPairUnitRepeatTemplates(ShapeNestingInput input, List<V4Template> templates, V4Template pair, ShapePolygon source, int count, V4Strategy strategy)
        {
            AddTemplate(templates, PairShelfTemplate(input, pair, source, count, strategy, 0));
            AddTemplate(templates, PairShelfTemplate(input, pair, source, count, strategy, 1));
            AddTemplate(templates, PairShelfTemplate(input, pair, source, count, strategy, 2));
        }

        private static void AddPairOrientations(List<V4Template> templates, V4Template pair, V4Strategy strategy, int mode)
        {
            foreach (int rotation in PairOrientationOrder(strategy, mode))
            {
                AddTemplate(templates, PairOrientedTemplate(pair, rotation));
            }
        }

        private static V4Template PairOrientedTemplate(V4Template pair, int rotation)
        {
            V4Template template = RotateTemplate(pair, rotation);
            template.PairRotation = NormalizeRotation(rotation);
            return template;
        }

        private static V4Template RotateTemplate(V4Template template, int rotation)
        {
            return NormalizeTemplate(RotateAndNormalize(template.Polygons, rotation));
        }

        private static V4Template RotateTemplate(V4Template template, double rotation)
        {
            return NormalizeTemplate(RotateAndNormalize(template.Polygons, rotation));
        }

        private static V4Template BuildPairHugTemplate(ShapeNestingInput input, ShapePolygon source, V4Strategy strategy)
        {
            List<V4Template> templates = BuildPairHugTemplates(input, source, strategy, -1, true);
            return templates.Count > 0 ? templates[0] : null;
        }

        private static List<V4Template> BuildPairHugTemplates(ShapeNestingInput input, ShapePolygon source, V4Strategy strategy, int previewTypeIndex)
        {
            return BuildPairHugTemplates(input, source, strategy, previewTypeIndex, false);
        }

        private static List<V4Template> BuildPairHugTemplates(ShapeNestingInput input, ShapePolygon source, V4Strategy strategy, int previewTypeIndex, bool singleBestOnly)
        {
            if (singleBestOnly)
            {
                return BuildSinglePairHugTemplateFast(input, source, strategy, previewTypeIndex);
            }

            List<V4Template> candidates = new List<V4Template>();
            List<double> rotations = PairRotations(source, strategy);
            double partArea = Math.Max(1.0, source.Area());
            foreach (double firstRotation in rotations)
            {
                PumpUi(false);
                ShapePolygon first = RotatePolygonNormalize(source, firstRotation);
                ShapePolygon secondBase = RotatePolygonNormalize(source, firstRotation + 180.0);
                List<ShapePoint> offsets = PairSearchOffsets(first, secondBase);
                foreach (ShapePoint offset in offsets)
                {
                    PumpUi(false);
                    foreach (ShapePoint tightened in TightenPairOffsets(first, secondBase, offset))
                    {
                        PumpUi(false);
                        V4Template candidate;
                        double score;
                        if (!TryBuildPairTemplate(input, first, secondBase, tightened, partArea, out candidate, out score))
                        {
                            continue;
                        }
                        AddTemplate(candidates, candidate);
                    }
                }
            }

            List<V4Template> best = singleBestOnly ? SingleBestTemplate(candidates, partArea, strategy) : BestRatioTemplates(candidates, partArea);
            best.Sort(delegate (V4Template a, V4Template b)
            {
                return TemplateScore(a, strategy).CompareTo(TemplateScore(b, strategy));
            });
            if (previewTypeIndex >= 0 && best.Count > 0)
            {
                PreviewTemplate(input, previewTypeIndex, best[0], "hug selected " + input.PartTypes[previewTypeIndex].Name + " x2");
            }
            if (best.Count > TemplateVariantLimit)
            {
                best.RemoveRange(TemplateVariantLimit, best.Count - TemplateVariantLimit);
            }
            return best;
        }

        private static List<V4Template> BuildSinglePairHugTemplateFast(ShapeNestingInput input, ShapePolygon source, V4Strategy strategy, int previewTypeIndex)
        {
            V4Template best = null;
            double bestRatio = double.NegativeInfinity;
            double bestScore = double.PositiveInfinity;
            double partArea = Math.Max(1.0, source.Area());
            foreach (double firstRotation in PairRotations(source, strategy))
            {
                PumpUi(false);
                ShapePolygon first = RotatePolygonNormalize(source, firstRotation);
                ShapePolygon secondBase = RotatePolygonNormalize(source, firstRotation + 180.0);
                foreach (ShapePoint offset in PairSearchOffsetsFast(first, secondBase))
                {
                    PumpUi(false);
                    foreach (ShapePoint tightened in TightenPairOffsetsFast(first, secondBase, offset))
                    {
                        V4Template candidate;
                        double score;
                        if (!TryBuildPairTemplate(input, first, secondBase, tightened, partArea, out candidate, out score))
                        {
                            continue;
                        }
                        double ratio = TemplateOccupiedRatio(candidate, partArea);
                        if (ratio > bestRatio + TemplateRatioTolerance ||
                            (Math.Abs(ratio - bestRatio) <= TemplateRatioTolerance && score < bestScore))
                        {
                            bestRatio = ratio;
                            bestScore = score;
                            best = candidate;
                        }
                    }
                }
            }

            List<V4Template> result = new List<V4Template>();
            AddTemplate(result, best);
            if (previewTypeIndex >= 0 && result.Count > 0)
            {
                PreviewTemplate(input, previewTypeIndex, result[0], "hug selected " + input.PartTypes[previewTypeIndex].Name + " x2");
            }
            return result;
        }

        private static List<ShapePoint> PairSearchOffsetsFast(ShapePolygon first, ShapePolygon second)
        {
            List<ShapePoint> nfpOffsets = PairNfpSearchOffsets(first, second, PairFastCandidateLimit);
            if (nfpOffsets.Count > 0)
            {
                return nfpOffsets;
            }

            List<ShapePoint> offsets = new List<ShapePoint>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            ShapeBounds firstBounds = first.Bounds();
            ShapeBounds secondBounds = second.Bounds();
            AddPairBoundsOffsets(offsets, seen, firstBounds, secondBounds);

            List<ShapePoint> fixedAnchors = LimitAnchors(HugAnchorPoints(first), 10);
            List<ShapePoint> movingAnchors = LimitAnchors(HugAnchorPoints(second), 10);
            foreach (ShapePoint fixedPoint in fixedAnchors)
            {
                PumpUi(false);
                foreach (ShapePoint movingPoint in movingAnchors)
                {
                    AddOffset(offsets, seen, fixedPoint.X - movingPoint.X + PairHugGap, fixedPoint.Y - movingPoint.Y);
                    AddOffset(offsets, seen, fixedPoint.X - movingPoint.X - PairHugGap, fixedPoint.Y - movingPoint.Y);
                    AddOffset(offsets, seen, fixedPoint.X - movingPoint.X, fixedPoint.Y - movingPoint.Y + PairHugGap);
                    AddOffset(offsets, seen, fixedPoint.X - movingPoint.X, fixedPoint.Y - movingPoint.Y - PairHugGap);
                    if (offsets.Count >= PairFastCandidateLimit)
                    {
                        SortPairOffsets(offsets, firstBounds, secondBounds);
                        return offsets;
                    }
                }
            }
            SortPairOffsets(offsets, firstBounds, secondBounds);
            if (offsets.Count > PairFastCandidateLimit)
            {
                offsets.RemoveRange(PairFastCandidateLimit, offsets.Count - PairFastCandidateLimit);
            }
            return offsets;
        }

        private static List<ShapePoint> PairNfpSearchOffsets(ShapePolygon first, ShapePolygon second, int limit)
        {
            List<ShapePoint> offsets = new List<ShapePoint>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            foreach (ShapePoint offset in PairNfpBoundaryOffsets(first, second))
            {
                PumpUi(false);
                if (PairOverlaps(first, second, offset))
                {
                    continue;
                }
                AddOffset(offsets, seen, offset.X, offset.Y);
                if (offsets.Count >= limit)
                {
                    break;
                }
            }
            SortPairOffsets(offsets, first.Bounds(), second.Bounds());
            return offsets;
        }

        private static void AddPairBoundsOffsets(List<ShapePoint> offsets, Dictionary<string, bool> seen, ShapeBounds firstBounds, ShapeBounds secondBounds)
        {
            AddOffset(offsets, seen, firstBounds.MaxX - secondBounds.MinX + PairHugGap, firstBounds.MinY - secondBounds.MinY);
            AddOffset(offsets, seen, firstBounds.MinX - secondBounds.MaxX - PairHugGap, firstBounds.MinY - secondBounds.MinY);
            AddOffset(offsets, seen, firstBounds.MinX - secondBounds.MinX, firstBounds.MaxY - secondBounds.MinY + PairHugGap);
            AddOffset(offsets, seen, firstBounds.MinX - secondBounds.MinX, firstBounds.MinY - secondBounds.MaxY - PairHugGap);
            AddOffset(offsets, seen, firstBounds.MaxX - secondBounds.MinX + PairHugGap, firstBounds.MaxY - secondBounds.MaxY);
            AddOffset(offsets, seen, firstBounds.MinX - secondBounds.MaxX - PairHugGap, firstBounds.MaxY - secondBounds.MaxY);
            AddOffset(offsets, seen, firstBounds.MaxX - secondBounds.MaxX, firstBounds.MaxY - secondBounds.MinY + PairHugGap);
            AddOffset(offsets, seen, firstBounds.MaxX - secondBounds.MaxX, firstBounds.MinY - secondBounds.MaxY - PairHugGap);
        }

        private static List<ShapePoint> TightenPairOffsetsFast(ShapePolygon first, ShapePolygon secondBase, ShapePoint offset)
        {
            List<ShapePoint> offsets = new List<ShapePoint>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            AddOffset(offsets, seen, offset.X, offset.Y);
            ShapePoint center = PairCenterDirection(first, secondBase, offset);
            ShapePoint diagonal = SlidePairOffset(first, secondBase, offset, center.X, center.Y);
            ShapePoint horizontal = SlidePairOffset(first, secondBase, offset, center.X, 0.0);
            ShapePoint vertical = SlidePairOffset(first, secondBase, offset, 0.0, center.Y);
            AddOffset(offsets, seen, diagonal.X, diagonal.Y);
            AddOffset(offsets, seen, horizontal.X, horizontal.Y);
            AddOffset(offsets, seen, vertical.X, vertical.Y);
            return offsets;
        }

        private static List<ShapePoint> PairSearchOffsets(ShapePolygon first, ShapePolygon second)
        {
            List<ShapePoint> nfpOffsets = PairNfpSearchOffsets(first, second, GroupCandidateLimit * 2);
            if (nfpOffsets.Count > 0)
            {
                return nfpOffsets;
            }

            List<ShapePoint> offsets = PairSearchOffsets(first.Bounds(), second.Bounds());
            Dictionary<string, bool> seen = OffsetSeen(offsets);
            List<ShapePoint> fixedAnchors = LimitAnchors(HugAnchorPoints(first), 20);
            List<ShapePoint> movingAnchors = LimitAnchors(HugAnchorPoints(second), 20);
            foreach (ShapePoint fixedPoint in fixedAnchors)
            {
                PumpUi(false);
                foreach (ShapePoint movingPoint in movingAnchors)
                {
                    AddOffset(offsets, seen, fixedPoint.X - movingPoint.X + PairHugGap, fixedPoint.Y - movingPoint.Y);
                    AddOffset(offsets, seen, fixedPoint.X - movingPoint.X - PairHugGap, fixedPoint.Y - movingPoint.Y);
                    AddOffset(offsets, seen, fixedPoint.X - movingPoint.X, fixedPoint.Y - movingPoint.Y + PairHugGap);
                    AddOffset(offsets, seen, fixedPoint.X - movingPoint.X, fixedPoint.Y - movingPoint.Y - PairHugGap);
                }
            }
            SortPairOffsets(offsets, first.Bounds(), second.Bounds());
            if (offsets.Count > GroupCandidateLimit * 2)
            {
                offsets.RemoveRange(GroupCandidateLimit * 2, offsets.Count - GroupCandidateLimit * 2);
            }
            return offsets;
        }

        private static List<ShapePoint> TightenPairOffsets(ShapePolygon first, ShapePolygon secondBase, ShapePoint offset)
        {
            List<ShapePoint> offsets = new List<ShapePoint>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            ShapeBounds firstBounds = first.Bounds();
            ShapeBounds secondBounds = secondBase.Bounds();
            double step = Math.Max(1.0, Math.Min(
                Math.Min(firstBounds.Width, firstBounds.Height),
                Math.Min(secondBounds.Width, secondBounds.Height)) / 18.0);
            for (int ix = -1; ix <= 1; ix++)
            {
                for (int iy = -1; iy <= 1; iy++)
                {
                    AddOffset(offsets, seen, offset.X + ix * step, offset.Y + iy * step);
                }
            }
            ShapePoint center = PairCenterDirection(first, secondBase, offset);
            List<ShapePoint> starts = new List<ShapePoint>(offsets);
            foreach (ShapePoint start in starts)
            {
                PumpUi(false);
                ShapePoint diagonal = SlidePairOffset(first, secondBase, start, center.X, center.Y);
                ShapePoint horizontal = SlidePairOffset(first, secondBase, start, center.X, 0.0);
                ShapePoint vertical = SlidePairOffset(first, secondBase, start, 0.0, center.Y);
                AddOffset(offsets, seen, diagonal.X, diagonal.Y);
                AddOffset(offsets, seen, horizontal.X, horizontal.Y);
                AddOffset(offsets, seen, vertical.X, vertical.Y);
            }
            return offsets;
        }

        private static ShapePoint PairCenterDirection(ShapePolygon first, ShapePolygon secondBase, ShapePoint offset)
        {
            ShapeBounds fb = first.Bounds();
            ShapeBounds sb = secondBase.Translate(offset.X, offset.Y).Bounds();
            double dx = (fb.MinX + fb.MaxX - sb.MinX - sb.MaxX) / 2.0;
            double dy = (fb.MinY + fb.MaxY - sb.MinY - sb.MaxY) / 2.0;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length <= 1e-9)
            {
                return new ShapePoint(0.0, 0.0);
            }
            return new ShapePoint(dx / length, dy / length);
        }

        private static ShapePoint SlidePairOffset(ShapePolygon first, ShapePolygon secondBase, ShapePoint start, double dx, double dy)
        {
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length <= 1e-9 || PairOverlaps(first, secondBase, start))
            {
                return start;
            }

            dx /= length;
            dy /= length;
            ShapeBounds fb = first.Bounds();
            ShapeBounds sb = secondBase.Bounds();
            double maxTravel = Math.Max(fb.Width + sb.Width, fb.Height + sb.Height);
            double low = 0.0;
            double high = 2.0;
            while (high <= maxTravel)
            {
                PumpUi(false);
                ShapePoint candidate = new ShapePoint(start.X + dx * high, start.Y + dy * high);
                if (PairOverlaps(first, secondBase, candidate))
                {
                    break;
                }
                low = high;
                high *= 2.0;
            }
            if (high > maxTravel)
            {
                return new ShapePoint(start.X + dx * low, start.Y + dy * low);
            }
            for (int i = 0; i < 16; i++)
            {
                PumpUi(false);
                double mid = (low + high) / 2.0;
                ShapePoint candidate = new ShapePoint(start.X + dx * mid, start.Y + dy * mid);
                if (PairOverlaps(first, secondBase, candidate))
                {
                    high = mid;
                }
                else
                {
                    low = mid;
                }
            }
            return new ShapePoint(start.X + dx * Math.Max(0.0, low), start.Y + dy * Math.Max(0.0, low));
        }

        private static bool PairOverlaps(ShapePolygon first, ShapePolygon secondBase, ShapePoint offset)
        {
            ShapeBounds firstBounds = first.Bounds();
            ShapePolygon second = secondBase.Translate(offset.X, offset.Y);
            ShapeBounds secondBounds = second.Bounds();
            return ShapeCollision.BoundsOverlap(firstBounds, secondBounds) && ShapeCollision.Intersects(first, firstBounds, second, secondBounds);
        }

        private static bool TryBuildPairTemplate(ShapeNestingInput input, ShapePolygon first, ShapePolygon secondBase, ShapePoint offset, double partArea, out V4Template template, out double score)
        {
            template = null;
            score = double.PositiveInfinity;
            ShapeBounds firstBounds = first.Bounds();
            ShapePolygon second = secondBase.Translate(offset.X, offset.Y);
            ShapeBounds secondBounds = second.Bounds();
            if (ShapeCollision.BoundsOverlap(firstBounds, secondBounds) && ShapeCollision.Intersects(first, firstBounds, second, secondBounds))
            {
                return false;
            }

            ShapePoint compactedOffset = CompactPairOffset(first, secondBase, offset, partArea);
            second = secondBase.Translate(compactedOffset.X, compactedOffset.Y);
            secondBounds = second.Bounds();
            if (ShapeCollision.BoundsOverlap(firstBounds, secondBounds) && ShapeCollision.Intersects(first, firstBounds, second, secondBounds))
            {
                return false;
            }

            List<ShapePolygon> normalized = NormalizePolygons(new List<ShapePolygon> { first, second });
            ShapeBounds bounds = BoundsOf(normalized);
            if (!TemplateCanFitBoardInUsefulOrientation(input, normalized))
            {
                return false;
            }

            double pairArea = bounds.Width * bounds.Height;
            double occupiedRatio = (partArea * 2.0) / Math.Max(1.0, pairArea);
            double minEdge = MinEdgeDistance(normalized[0], normalized[1]);
            double edgeNear = EdgeNearReward(normalized[0], normalized[1], 16.0);
            double centerPenalty = PairCenterDistance(normalized[0].Bounds(), normalized[1].Bounds());
            score = -occupiedRatio * 1000000000.0 +
                pairArea * 0.001 +
                minEdge * 5000.0 +
                Math.Max(bounds.Width, bounds.Height) * 0.1 +
                centerPenalty * 0.1 -
                edgeNear * 10.0;
            template = NormalizeTemplate(normalized);
            return true;
        }

        private static ShapePoint CompactPairOffset(ShapePolygon first, ShapePolygon secondBase, ShapePoint start, double partArea)
        {
            ShapePoint best = start;
            double bestScore = PairCompactScore(first, secondBase, best, partArea);
            ShapeBounds firstBounds = first.Bounds();
            ShapeBounds secondBounds = secondBase.Bounds();
            double span = Math.Max(firstBounds.Width + secondBounds.Width, firstBounds.Height + secondBounds.Height);
            double[] steps = new double[]
            {
                Math.Max(20.0, span / 5.0),
                Math.Max(5.0, span / 18.0),
                Math.Max(1.0, span / 80.0),
                Math.Max(0.2, PairHugGap)
            };

            foreach (double step in steps)
            {
                bool improved = true;
                int guard = 0;
                while (improved && guard < 60)
                {
                    PumpUi(false);
                    improved = false;
                    guard++;
                    ShapePoint[] trials = new ShapePoint[]
                    {
                        new ShapePoint(best.X - step, best.Y),
                        new ShapePoint(best.X + step, best.Y),
                        new ShapePoint(best.X, best.Y - step),
                        new ShapePoint(best.X, best.Y + step),
                        new ShapePoint(best.X - step, best.Y - step),
                        new ShapePoint(best.X - step, best.Y + step),
                        new ShapePoint(best.X + step, best.Y - step),
                        new ShapePoint(best.X + step, best.Y + step)
                    };
                    foreach (ShapePoint trial in trials)
                    {
                        if (PairOverlaps(first, secondBase, trial))
                        {
                            continue;
                        }
                        double score = PairCompactScore(first, secondBase, trial, partArea);
                        if (score < bestScore - 1e-6)
                        {
                            bestScore = score;
                            best = trial;
                            improved = true;
                        }
                    }
                }
            }
            return best;
        }

        private static double PairCompactScore(ShapePolygon first, ShapePolygon secondBase, ShapePoint offset, double partArea)
        {
            ShapePolygon second = secondBase.Translate(offset.X, offset.Y);
            if (PairOverlaps(first, secondBase, offset))
            {
                return double.PositiveInfinity;
            }
            List<ShapePolygon> normalized = NormalizePolygons(new List<ShapePolygon> { first, second });
            ShapeBounds bounds = BoundsOf(normalized);
            double boxArea = Math.Max(1.0, bounds.Width * bounds.Height);
            double ratio = (partArea * 2.0) / boxArea;
            return -ratio * 1000000000.0 +
                boxArea +
                MinEdgeDistance(normalized[0], normalized[1]) * 5000.0 +
                PairCenterDistance(normalized[0].Bounds(), normalized[1].Bounds()) * 0.1;
        }

        private static bool PairCanFitBoardInEitherOrientation(ShapeNestingInput input, ShapeBounds bounds)
        {
            return TemplateCanFitBoardInAnyOrientation(input, bounds);
        }

        private static bool TemplateCanFitBoardInAnyOrientation(ShapeNestingInput input, ShapeBounds bounds)
        {
            return (bounds.Width <= input.BoardWidth + 1e-6 && bounds.Height <= input.BoardHeight + 1e-6) ||
                (bounds.Height <= input.BoardWidth + 1e-6 && bounds.Width <= input.BoardHeight + 1e-6);
        }

        private static List<V4Template> BestRatioTemplates(List<V4Template> templates, double partArea)
        {
            List<V4Template> candidates = new List<V4Template>();
            double bestRatio = double.NegativeInfinity;
            foreach (V4Template template in templates)
            {
                if (template == null)
                {
                    continue;
                }
                double ratio = TemplateOccupiedRatio(template, partArea);
                if (ratio > bestRatio + TemplateRatioTolerance)
                {
                    bestRatio = ratio;
                    candidates.Clear();
                }
                if (Math.Abs(ratio - bestRatio) <= TemplateRatioTolerance)
                {
                    AddTemplate(candidates, template);
                }
            }
            return DiverseBestTemplates(candidates, partArea, TemplateVariantLimit);
        }

        private static List<V4Template> SingleBestTemplate(List<V4Template> templates, double partArea, V4Strategy strategy)
        {
            V4Template best = null;
            double bestRatio = double.NegativeInfinity;
            double bestScore = double.PositiveInfinity;
            foreach (V4Template template in templates)
            {
                if (template == null)
                {
                    continue;
                }
                double ratio = TemplateOccupiedRatio(template, partArea);
                double score = TemplateScore(template, strategy);
                if (ratio > bestRatio + TemplateRatioTolerance ||
                    (Math.Abs(ratio - bestRatio) <= TemplateRatioTolerance && score < bestScore))
                {
                    bestRatio = ratio;
                    bestScore = score;
                    best = template;
                }
            }

            List<V4Template> result = new List<V4Template>();
            AddTemplate(result, best);
            return result;
        }

        private static List<V4Template> DiverseBestTemplates(List<V4Template> templates, double partArea, int limit)
        {
            List<V4Template> sorted = new List<V4Template>();
            foreach (V4Template template in templates)
            {
                if (template != null)
                {
                    sorted.Add(template);
                }
            }
            sorted.Sort(delegate (V4Template a, V4Template b)
            {
                int cmp = TemplateDiversityBucket(a).CompareTo(TemplateDiversityBucket(b));
                if (cmp != 0)
                {
                    return cmp;
                }
                return TemplateCompactTieScore(a).CompareTo(TemplateCompactTieScore(b));
            });

            List<V4Template> selected = new List<V4Template>();
            Dictionary<string, bool> buckets = new Dictionary<string, bool>();
            foreach (V4Template template in sorted)
            {
                if (selected.Count >= limit)
                {
                    break;
                }
                string bucket = TemplateDiversityBucket(template);
                if (buckets.ContainsKey(bucket))
                {
                    continue;
                }
                AddTemplate(selected, template);
                buckets[bucket] = true;
            }
            foreach (V4Template template in sorted)
            {
                if (selected.Count >= limit)
                {
                    break;
                }
                AddTemplate(selected, template);
            }
            return selected;
        }

        private static List<V4Template> DiverseRatioTemplates(List<V4Template> templates, double partArea, V4Strategy strategy, int limit)
        {
            List<V4Template> sorted = new List<V4Template>();
            foreach (V4Template template in templates)
            {
                if (template != null)
                {
                    sorted.Add(template);
                }
            }
            if (sorted.Count == 0)
            {
                return sorted;
            }

            sorted.Sort(delegate (V4Template a, V4Template b)
            {
                int cmp = CompareDescending(TemplateOccupiedRatio(a, partArea), TemplateOccupiedRatio(b, partArea), TemplateRatioTolerance);
                if (cmp != 0)
                {
                    return cmp;
                }
                return TemplateScore(a, strategy).CompareTo(TemplateScore(b, strategy));
            });

            double bestRatio = TemplateOccupiedRatio(sorted[0], partArea);
            List<V4Template> sameBest = new List<V4Template>();
            foreach (V4Template template in sorted)
            {
                double ratio = TemplateOccupiedRatio(template, partArea);
                if (Math.Abs(ratio - bestRatio) <= TemplateRatioTolerance)
                {
                    AddTemplate(sameBest, template);
                }
            }
            List<V4Template> selected = DiverseBestTemplates(sameBest, partArea, limit);
            if (selected.Count == 0 && sorted.Count > 0)
            {
                AddTemplate(selected, sorted[0]);
            }
            return selected;
        }

        private static List<V4Template> DiverseRegularComboTemplates(List<V4Template> templates, double partArea, V4Strategy strategy, int limit)
        {
            return SelectDimensionDiverseTemplates(templates, strategy, limit);
        }

        private static List<V4Template> SelectDimensionDiverseTemplates(List<V4Template> templates, V4Strategy strategy, int limit)
        {
            Dictionary<string, Dictionary<string, V4Template>> bestByMixLayout = new Dictionary<string, Dictionary<string, V4Template>>();
            foreach (V4Template template in templates)
            {
                if (template == null)
                {
                    continue;
                }
                string mix = OrientationMixBucket(template);
                string layout = TemplateLayoutSignature(template);
                Dictionary<string, V4Template> bestByLayout;
                if (!bestByMixLayout.TryGetValue(mix, out bestByLayout))
                {
                    bestByLayout = new Dictionary<string, V4Template>();
                    bestByMixLayout[mix] = bestByLayout;
                }
                V4Template existing;
                if (!bestByLayout.TryGetValue(layout, out existing) ||
                    HugSuccessScore(template, strategy) < HugSuccessScore(existing, strategy))
                {
                    bestByLayout[layout] = template;
                }
            }

            List<string> mixes = new List<string>(bestByMixLayout.Keys);
            mixes.Sort(CompareOrientationMixKeys);

            List<V4Template> selected = new List<V4Template>();
            foreach (string mix in mixes)
            {
                List<V4Template> group = new List<V4Template>(bestByMixLayout[mix].Values);
                group.Sort(delegate (V4Template a, V4Template b)
                {
                    int cmp = CompareDescending(TemplateFilledRatio(a), TemplateFilledRatio(b), TemplateRatioTolerance);
                    if (cmp != 0)
                    {
                        return cmp;
                    }
                    return HugSuccessScore(a, strategy).CompareTo(HugSuccessScore(b, strategy));
                });

                if (group.Count > 0 && selected.Count < limit)
                {
                    AddTemplate(selected, group[0]);
                }
            }

            if (selected.Count >= limit)
            {
                return selected;
            }

            return selected;
        }

        private static string OrientationMixBucket(V4Template template)
        {
            int horizontal = 0;
            int vertical = 0;
            int square = 0;
            List<ShapeBounds> units = TemplateUnitBounds(template);
            foreach (ShapeBounds unit in units)
            {
                char orientation = UnitOrientation(unit);
                if (orientation == 'H')
                {
                    horizontal++;
                }
                else if (orientation == 'V')
                {
                    vertical++;
                }
                else
                {
                    square++;
                }
            }
            int major = Math.Max(horizontal, vertical);
            int minor = Math.Min(horizontal, vertical);
            return units.Count.ToString(CultureInfo.InvariantCulture) + ":" +
                major.ToString(CultureInfo.InvariantCulture) + ":" +
                minor.ToString(CultureInfo.InvariantCulture) + ":" +
                square.ToString(CultureInfo.InvariantCulture);
        }

        private static int CompareOrientationMixKeys(string a, string b)
        {
            int au;
            int amajor;
            int aminor;
            int @as;
            int bu;
            int bmajor;
            int bminor;
            int bs;
            ParseOrientationMixKey(a, out au, out amajor, out aminor, out @as);
            ParseOrientationMixKey(b, out bu, out bmajor, out bminor, out bs);
            int cmp = au.CompareTo(bu);
            if (cmp != 0)
            {
                return cmp;
            }
            cmp = bmajor.CompareTo(amajor);
            if (cmp != 0)
            {
                return cmp;
            }
            cmp = aminor.CompareTo(bminor);
            if (cmp != 0)
            {
                return cmp;
            }
            return @as.CompareTo(bs);
        }

        private static void ParseOrientationMixKey(string key, out int units, out int horizontal, out int vertical, out int square)
        {
            units = 0;
            horizontal = 0;
            vertical = 0;
            square = 0;
            if (string.IsNullOrEmpty(key))
            {
                return;
            }
            string[] parts = key.Split(':');
            if (parts.Length > 0)
            {
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out units);
            }
            if (parts.Length > 1)
            {
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out horizontal);
            }
            if (parts.Length > 2)
            {
                int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out vertical);
            }
            if (parts.Length > 3)
            {
                int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out square);
            }
        }

        private static string TemplateLayoutSignature(V4Template template)
        {
            List<ShapeBounds> units = TemplateUnitBounds(template);
            if (units.Count == 0)
            {
                return TemplateDimensionBucket(template);
            }

            List<double> xs = new List<double>();
            List<double> ys = new List<double>();
            double averageSmallSide = 0.0;
            foreach (ShapeBounds unit in units)
            {
                xs.Add((unit.MinX + unit.MaxX) / 2.0);
                ys.Add((unit.MinY + unit.MaxY) / 2.0);
                averageSmallSide += Math.Min(Math.Max(1.0, unit.Width), Math.Max(1.0, unit.Height));
            }
            averageSmallSide /= Math.Max(1, units.Count);
            double tolerance = Math.Max(8.0, averageSmallSide * 0.28);
            List<double> xClusters = AxisClusterCenters(xs, tolerance);
            List<double> yClusters = AxisClusterCenters(ys, tolerance);

            List<V4LayoutCell> cells = new List<V4LayoutCell>();
            foreach (ShapeBounds unit in units)
            {
                double cx = (unit.MinX + unit.MaxX) / 2.0;
                double cy = (unit.MinY + unit.MaxY) / 2.0;
                cells.Add(new V4LayoutCell
                {
                    X = AxisClusterIndex(xClusters, cx),
                    Y = AxisClusterIndex(yClusters, cy),
                    Orientation = UnitOrientation(unit)
                });
            }

            return units.Count.ToString(CultureInfo.InvariantCulture) + ":" + CanonicalLayout(cells);
        }

        private static List<ShapeBounds> TemplateUnitBounds(V4Template template)
        {
            List<ShapeBounds> units = new List<ShapeBounds>();
            if (template == null || template.Polygons == null || template.Polygons.Count == 0)
            {
                return units;
            }

            if (template.Polygons.Count >= 2 && template.Polygons.Count % 2 == 0)
            {
                for (int i = 0; i + 1 < template.Polygons.Count; i += 2)
                {
                    units.Add(BoundsOf(new List<ShapePolygon> { template.Polygons[i], template.Polygons[i + 1] }));
                }
                return units;
            }

            foreach (ShapePolygon polygon in template.Polygons)
            {
                units.Add(polygon.Bounds());
            }
            return units;
        }

        private static char UnitOrientation(ShapeBounds bounds)
        {
            if (bounds.Width > bounds.Height * 1.12)
            {
                return 'H';
            }
            if (bounds.Height > bounds.Width * 1.12)
            {
                return 'V';
            }
            return 'S';
        }

        private static List<double> AxisClusterCenters(List<double> values, double tolerance)
        {
            List<double> sorted = new List<double>(values);
            sorted.Sort();
            List<double> clusters = new List<double>();
            foreach (double value in sorted)
            {
                if (clusters.Count == 0 || Math.Abs(value - clusters[clusters.Count - 1]) > tolerance)
                {
                    clusters.Add(value);
                }
                else
                {
                    clusters[clusters.Count - 1] = (clusters[clusters.Count - 1] + value) / 2.0;
                }
            }
            return clusters;
        }

        private static int AxisClusterIndex(List<double> clusters, double value)
        {
            int best = 0;
            double bestDistance = double.PositiveInfinity;
            for (int i = 0; i < clusters.Count; i++)
            {
                double distance = Math.Abs(clusters[i] - value);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }
            return best;
        }

        private static string CanonicalLayout(List<V4LayoutCell> cells)
        {
            string best = null;
            for (int transform = 0; transform < 8; transform++)
            {
                string key = LayoutTransformKey(cells, transform);
                if (best == null || string.CompareOrdinal(key, best) < 0)
                {
                    best = key;
                }
            }
            return best ?? string.Empty;
        }

        private static string LayoutTransformKey(List<V4LayoutCell> cells, int transform)
        {
            int maxX = 0;
            int maxY = 0;
            foreach (V4LayoutCell cell in cells)
            {
                maxX = Math.Max(maxX, cell.X);
                maxY = Math.Max(maxY, cell.Y);
            }

            List<string> parts = new List<string>();
            foreach (V4LayoutCell cell in cells)
            {
                int x = cell.X;
                int y = cell.Y;
                char orientation = cell.Orientation;
                if ((transform & 1) != 0)
                {
                    x = maxX - x;
                }
                if ((transform & 2) != 0)
                {
                    y = maxY - y;
                }
                if ((transform & 4) != 0)
                {
                    int oldX = x;
                    x = y;
                    y = oldX;
                    orientation = TransposeOrientation(orientation);
                }
                parts.Add(x.ToString(CultureInfo.InvariantCulture) + "," + y.ToString(CultureInfo.InvariantCulture) + "," + orientation.ToString());
            }
            parts.Sort();
            return string.Join("|", parts.ToArray());
        }

        private static char TransposeOrientation(char orientation)
        {
            if (orientation == 'H')
            {
                return 'V';
            }
            if (orientation == 'V')
            {
                return 'H';
            }
            return orientation;
        }

        private static string TemplateDimensionBucket(V4Template template)
        {
            double longSide = Math.Max(template.Bounds.Width, template.Bounds.Height);
            double shortSide = Math.Min(template.Bounds.Width, template.Bounds.Height);
            return template.Count.ToString(CultureInfo.InvariantCulture) + ":" +
                ((int)Math.Round(longSide * 10.0)).ToString(CultureInfo.InvariantCulture) + ":" +
                ((int)Math.Round(shortSide * 10.0)).ToString(CultureInfo.InvariantCulture);
        }

        private static string DimensionShapeBucket(V4Template template)
        {
            double longSide = Math.Max(1.0, Math.Max(template.Bounds.Width, template.Bounds.Height));
            double shortSide = Math.Max(1.0, Math.Min(template.Bounds.Width, template.Bounds.Height));
            double ratio = longSide / shortSide;
            string aspect = ratio > 2.2 ? "chain" : (ratio > 1.35 ? "rect" : "block");
            return aspect + ":" + RegularComboBucket(template);
        }

        private static double DimensionTemplateScore(V4Template template, V4Strategy strategy)
        {
            double longSide = Math.Max(template.Bounds.Width, template.Bounds.Height);
            double shortSide = Math.Min(template.Bounds.Width, template.Bounds.Height);
            return longSide * shortSide +
                longSide * 80.0 +
                shortSide * 20.0 +
                StackRegularityPenalty(template.Polygons) +
                PairOrientationPenalty(template, strategy) * 2000.0;
        }

        private static double HugSuccessScore(V4Template template, V4Strategy strategy)
        {
            double filledRatio = TemplateFilledRatio(template);
            return -filledRatio * 1000000000.0 +
                template.Bounds.Width * template.Bounds.Height +
                StackRegularityPenalty(template.Polygons) +
                Math.Max(template.Bounds.Width, template.Bounds.Height) * 20.0 +
                PairOrientationPenalty(template, strategy) * 2000.0;
        }

        private static double TemplateFilledRatio(V4Template template)
        {
            if (template == null)
            {
                return 0.0;
            }
            double area = 0.0;
            foreach (ShapePolygon polygon in template.Polygons)
            {
                area += Math.Abs(polygon.Area());
            }
            double boxArea = Math.Max(1.0, template.Bounds.Width * template.Bounds.Height);
            return area / boxArea;
        }

        private static double RegularComboScore(V4Template template, V4Strategy strategy)
        {
            return template.Bounds.Width * template.Bounds.Height +
                StackRegularityPenalty(template.Polygons) +
                Math.Max(template.Bounds.Width, template.Bounds.Height) * 60.0 +
                PairOrientationPenalty(template, strategy) * 2000.0;
        }

        private static string RegularComboBucket(V4Template template)
        {
            int wide = 0;
            int tall = 0;
            int square = 0;
            List<double> xs = new List<double>();
            List<double> ys = new List<double>();
            foreach (ShapePolygon polygon in template.Polygons)
            {
                ShapeBounds bounds = polygon.Bounds();
                xs.Add((bounds.MinX + bounds.MaxX) / 2.0);
                ys.Add((bounds.MinY + bounds.MaxY) / 2.0);
                if (bounds.Width > bounds.Height * 1.12)
                {
                    wide++;
                }
                else if (bounds.Height > bounds.Width * 1.12)
                {
                    tall++;
                }
                else
                {
                    square++;
                }
            }

            string aspect;
            double width = Math.Max(1.0, template.Bounds.Width);
            double height = Math.Max(1.0, template.Bounds.Height);
            if (width > height * 1.45)
            {
                aspect = "long";
            }
            else if (height > width * 1.45)
            {
                aspect = "wide";
            }
            else
            {
                aspect = "block";
            }

            int xClusters = AxisClusterCount(xs, width);
            int yClusters = AxisClusterCount(ys, height);
            return template.Count.ToString(CultureInfo.InvariantCulture) + ":" +
                aspect + ":w" + wide.ToString(CultureInfo.InvariantCulture) +
                ":t" + tall.ToString(CultureInfo.InvariantCulture) +
                ":s" + square.ToString(CultureInfo.InvariantCulture) +
                ":gx" + xClusters.ToString(CultureInfo.InvariantCulture) +
                ":gy" + yClusters.ToString(CultureInfo.InvariantCulture);
        }

        private static int AxisClusterCount(List<double> values, double span)
        {
            if (values.Count == 0)
            {
                return 0;
            }
            values.Sort();
            double tolerance = Math.Max(8.0, span * 0.06);
            int clusters = 1;
            double center = values[0];
            foreach (double value in values)
            {
                if (Math.Abs(value - center) <= tolerance)
                {
                    center = (center + value) / 2.0;
                    continue;
                }
                clusters++;
                center = value;
            }
            return clusters;
        }

        private static string TemplateDiversityBucket(V4Template template)
        {
            double width = Math.Max(1.0, template.Bounds.Width);
            double height = Math.Max(1.0, template.Bounds.Height);
            string aspect;
            if (width > height * 1.35)
            {
                aspect = "long";
            }
            else if (height > width * 1.35)
            {
                aspect = "wide";
            }
            else
            {
                aspect = "block";
            }

            string size;
            double ratio = width / height;
            if (ratio > 2.2 || ratio < 1.0 / 2.2)
            {
                size = "chain";
            }
            else if (ratio > 1.35 || ratio < 1.0 / 1.35)
            {
                size = "rect";
            }
            else
            {
                size = "square";
            }

            return aspect + ":" + size + ":" + NormalizeRotation(template.PairRotation).ToString(CultureInfo.InvariantCulture);
        }

        private static void KeepBestRatioTemplate(List<V4Template> best, Dictionary<string, bool> seen, V4Template template, double ratio, double tieScore, ref double bestRatio, ref double bestTieScore)
        {
            if (template == null)
            {
                return;
            }
            if (ratio > bestRatio + TemplateRatioTolerance)
            {
                bestRatio = ratio;
                bestTieScore = tieScore;
                best.Clear();
                seen.Clear();
            }
            else if (Math.Abs(ratio - bestRatio) <= TemplateRatioTolerance)
            {
                if (tieScore < bestTieScore - 1.0)
                {
                    bestTieScore = tieScore;
                }
            }
            else
            {
                return;
            }

            string key = TemplateKey(template);
            if (!seen.ContainsKey(key))
            {
                seen.Add(key, true);
                best.Add(template);
            }
        }

        private static double TemplateOccupiedRatio(V4Template template, double singlePartArea)
        {
            double boxArea = Math.Max(1.0, template.Bounds.Width * template.Bounds.Height);
            return singlePartArea * template.Count / boxArea;
        }

        private static double TemplateCompactTieScore(V4Template template)
        {
            return template.Bounds.Width * template.Bounds.Height +
                Math.Max(template.Bounds.Width, template.Bounds.Height) * 0.1 +
                Math.Abs(template.Bounds.Width - template.Bounds.Height) * 0.01;
        }

        private static double PairCenterDistance(ShapeBounds a, ShapeBounds b)
        {
            double ax = (a.MinX + a.MaxX) / 2.0;
            double ay = (a.MinY + a.MaxY) / 2.0;
            double bx = (b.MinX + b.MaxX) / 2.0;
            double by = (b.MinY + b.MaxY) / 2.0;
            double dx = ax - bx;
            double dy = ay - by;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static V4Template PairUnitRepeatTemplate(ShapeNestingInput input, V4Template pair, int count, int pairRotation, bool horizontal)
        {
            if (pair == null || count < 2)
            {
                return null;
            }

            List<ShapePolygon> unit = RotateAndNormalize(pair.Polygons, pairRotation);
            List<ShapePolygon> polygons = new List<ShapePolygon>(unit);
            int fullUnits = count / 2;
            int mode = horizontal ? 1 : 2;
            for (int i = 1; i < fullUnits; i++)
            {
                PumpUi(false);
                List<ShapePolygon> moved;
                if (!BestGroupAddition(input, polygons, new List<List<ShapePolygon>> { unit }, mode, out moved))
                {
                    return null;
                }
                polygons.AddRange(moved);
                polygons = NormalizePolygons(polygons);
            }

            if (count % 2 == 1)
            {
                PumpUi(false);
                List<ShapePolygon> moved;
                if (!BestGroupAddition(input, polygons, new List<List<ShapePolygon>> { new List<ShapePolygon> { unit[0] } }, mode, out moved))
                {
                    return null;
                }
                polygons.AddRange(moved);
            }

            V4Template template = NormalizeTemplate(polygons);
            if (template.Bounds.Width > input.BoardWidth + 1e-6 || template.Bounds.Height > input.BoardHeight + 1e-6)
            {
                return null;
            }
            template.PairRotation = NormalizeRotation(pairRotation);
            return template;
        }

        private static V4Template PairShelfTemplate(ShapeNestingInput input, V4Template pair, ShapePolygon source, int count, V4Strategy strategy, int pattern)
        {
            if (pair == null || count < 2)
            {
                return null;
            }

            List<ShapePolygon> polygons = new List<ShapePolygon>();
            double x = 0.0;
            double y = 0.0;
            double rowHeight = 0.0;
            int fullUnits = count / 2;
            int preferred = NormalizeRotation(strategy.MainRotation) == 90 ? 90 : 0;
            int alternate = preferred == 90 ? 0 : 90;
            int rotation0Count = 0;
            int rotation90Count = 0;

            for (int i = 0; i < fullUnits; i++)
            {
                int rotation = PairShelfRotation(i, preferred, alternate, pattern);
                List<ShapePolygon> unit = RotateAndNormalize(pair.Polygons, rotation);
                ShapeBounds unitBounds = BoundsOf(unit);
                if (x > 1e-6 && x + unitBounds.Width > input.BoardWidth + 1e-6)
                {
                    x = 0.0;
                    y += rowHeight + Gap;
                    rowHeight = 0.0;
                }
                if (unitBounds.Width > input.BoardWidth + 1e-6 || y + unitBounds.Height > input.BoardHeight + 1e-6)
                {
                    return null;
                }
                foreach (ShapePolygon polygon in unit)
                {
                    polygons.Add(polygon.Translate(x, y));
                }
                if (NormalizeRotation(rotation) == 90)
                {
                    rotation90Count++;
                }
                else
                {
                    rotation0Count++;
                }
                x += unitBounds.Width + Gap;
                rowHeight = Math.Max(rowHeight, unitBounds.Height);
            }

            if (count % 2 == 1)
            {
                ShapePolygon single = source.RotateNormalize(preferred);
                ShapeBounds singleBounds = single.Bounds();
                if (x > 1e-6 && x + singleBounds.Width > input.BoardWidth + 1e-6)
                {
                    x = 0.0;
                    y += rowHeight + Gap;
                    rowHeight = 0.0;
                }
                if (singleBounds.Width > input.BoardWidth + 1e-6 || y + singleBounds.Height > input.BoardHeight + 1e-6)
                {
                    return null;
                }
                polygons.Add(single.Translate(x - singleBounds.MinX, y - singleBounds.MinY));
            }

            V4Template template = NormalizeTemplate(polygons);
            template.PairRotation = rotation90Count > rotation0Count ? 90 : 0;
            template.ShelfLayout = true;
            return template;
        }

        private static int PairShelfRotation(int index, int preferred, int alternate, int pattern)
        {
            if (pattern == 1)
            {
                return index % 3 == 2 ? alternate : preferred;
            }
            if (pattern == 2)
            {
                return index % 3 == 1 ? preferred : alternate;
            }
            return index % 2 == 0 ? preferred : alternate;
        }

        private static List<double> PairRotations(ShapePolygon source, V4Strategy strategy)
        {
            List<double> rotations = new List<double>();
            AddAngles(rotations, new double[] { 0.0, 90.0, 180.0, 270.0 });
            foreach (double angle in MinBoundingBoxAngles(new List<ShapePolygon> { source }))
            {
                AddAngle(rotations, angle);
                AddAngle(rotations, angle + 90.0);
                AddAngle(rotations, angle + 180.0);
                AddAngle(rotations, angle + 270.0);
            }
            return rotations;
        }

        private static int[] PairOrientationOrder(V4Strategy strategy, int mode)
        {
            if (mode == 2 || NormalizeRotation(strategy.MainRotation) == 90)
            {
                return new int[] { 90, 0 };
            }
            return new int[] { 0, 90 };
        }

        private static void AddRotation(List<int> rotations, int rotation)
        {
            int normalized = NormalizeRotation(rotation);
            foreach (int existing in rotations)
            {
                if (existing == normalized)
                {
                    return;
                }
            }
            rotations.Add(normalized);
        }

        private static List<ShapePoint> PairSearchOffsets(ShapeBounds firstBounds, ShapeBounds secondBounds)
        {
            List<ShapePoint> offsets = new List<ShapePoint>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            double step = Math.Max(4.0, Math.Min(firstBounds.Width + secondBounds.Width, firstBounds.Height + secondBounds.Height) / 45.0);
            double xStart = firstBounds.MinX - secondBounds.MaxX - 0.5;
            double xEnd = firstBounds.MaxX - secondBounds.MinX + 0.5;
            double yStart = firstBounds.MinY - secondBounds.MaxY - 0.5;
            double yEnd = firstBounds.MaxY - secondBounds.MinY + 0.5;
            for (double x = xStart; x <= xEnd + 1e-6; x += step)
            {
                PumpUi(false);
                for (double y = yStart; y <= yEnd + 1e-6; y += step)
                {
                    AddOffset(offsets, seen, x, y);
                }
            }

            AddOffset(offsets, seen, firstBounds.MaxX - secondBounds.MinX, firstBounds.MinY - secondBounds.MinY);
            AddOffset(offsets, seen, firstBounds.MinX - secondBounds.MaxX, firstBounds.MinY - secondBounds.MinY);
            AddOffset(offsets, seen, firstBounds.MinX - secondBounds.MinX, firstBounds.MaxY - secondBounds.MinY);
            AddOffset(offsets, seen, firstBounds.MinX - secondBounds.MinX, firstBounds.MinY - secondBounds.MaxY);
            AddOffset(offsets, seen, firstBounds.MaxX - secondBounds.MinX, firstBounds.MaxY - secondBounds.MinY);
            AddOffset(offsets, seen, firstBounds.MinX - secondBounds.MaxX, firstBounds.MaxY - secondBounds.MinY);

            SortPairOffsets(offsets, firstBounds, secondBounds);
            if (offsets.Count > GroupCandidateLimit)
            {
                offsets.RemoveRange(GroupCandidateLimit, offsets.Count - GroupCandidateLimit);
            }
            return offsets;
        }

        private static Dictionary<string, bool> OffsetSeen(List<ShapePoint> offsets)
        {
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            foreach (ShapePoint point in offsets)
            {
                string key = ((int)Math.Round(point.X * 100.0)).ToString(CultureInfo.InvariantCulture) + "," + ((int)Math.Round(point.Y * 100.0)).ToString(CultureInfo.InvariantCulture);
                if (!seen.ContainsKey(key))
                {
                    seen.Add(key, true);
                }
            }
            return seen;
        }

        private static void SortPairOffsets(List<ShapePoint> offsets, ShapeBounds firstBounds, ShapeBounds secondBounds)
        {
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
        }

        private static V4Template PairBlockTemplate(ShapeNestingInput input, V4Template pair, ShapePolygon source, int count, V4Strategy strategy, int mode)
        {
            if (pair == null || count < 2)
            {
                return null;
            }

            List<ShapePolygon> placed = PairSeed(pair, strategy, mode);
            while (placed.Count < count)
            {
                bool usePair = placed.Count + 2 <= count;
                List<List<ShapePolygon>> options = usePair ? PairOptions(pair, strategy, mode) : SingleOptions(source, strategy, mode);
                List<ShapePolygon> moved;
                if (!BestGroupAddition(input, placed, options, mode, out moved))
                {
                    if (usePair)
                    {
                        break;
                    }
                    options = SingleOptions(source, strategy, mode + 1);
                    if (!BestGroupAddition(input, placed, options, mode, out moved))
                    {
                        break;
                    }
                }
                placed.AddRange(moved);
                placed = NormalizePolygons(placed);
            }

            if (placed.Count != count)
            {
                return null;
            }
            V4Template template = NormalizeTemplate(placed);
            template.PairRotation = PairOrientationOrder(strategy, mode)[0];
            return template;
        }

        private static List<ShapePolygon> PairSeed(V4Template pair, V4Strategy strategy, int mode)
        {
            int rotation = PairOrientationOrder(strategy, mode)[0];
            return RotateAndNormalize(pair.Polygons, rotation);
        }

        private static List<List<ShapePolygon>> PairOptions(V4Template pair, V4Strategy strategy, int mode)
        {
            int[] rotations = PairOrientationOrder(strategy, mode);
            List<List<ShapePolygon>> options = new List<List<ShapePolygon>>();
            foreach (int rotation in rotations)
            {
                options.Add(RotateAndNormalize(pair.Polygons, rotation));
            }
            return options;
        }

        private static List<List<ShapePolygon>> SingleOptions(ShapePolygon source, V4Strategy strategy, int mode)
        {
            int[] rotations = PairOrientationOrder(strategy, mode);
            List<List<ShapePolygon>> options = new List<List<ShapePolygon>>();
            foreach (int rotation in rotations)
            {
                options.Add(new List<ShapePolygon> { source.RotateNormalize(rotation) });
            }
            return options;
        }

        private static bool BestGroupAddition(ShapeNestingInput input, List<ShapePolygon> placed, List<List<ShapePolygon>> movingOptions, int mode, out List<ShapePolygon> bestMoved)
        {
            bestMoved = null;
            double bestScore = double.PositiveInfinity;
            List<ShapePolygon> anchors = ChainAnchors(placed, mode);
            foreach (List<ShapePolygon> option in movingOptions)
            {
                List<ShapePolygon> normalized = NormalizePolygons(option);
                List<ShapePoint> candidates = GroupCandidateOffsets(anchors, normalized);
                foreach (ShapePoint candidate in candidates)
                {
                    List<ShapePolygon> moved = TranslatePolygons(normalized, candidate.X, candidate.Y);
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
                    double near = GroupNearReward(moved, placed);
                    double contact = GroupContactReward(moved, placed);
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

        private static List<ShapePolygon> ChainAnchors(List<ShapePolygon> placed, int mode)
        {
            if (placed.Count == 0)
            {
                return placed;
            }

            double minX = double.PositiveInfinity;
            double minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            double maxY = double.NegativeInfinity;
            foreach (ShapePolygon polygon in placed)
            {
                ShapeBounds b = polygon.Bounds();
                minX = Math.Min(minX, b.MinX);
                minY = Math.Min(minY, b.MinY);
                maxX = Math.Max(maxX, b.MaxX);
                maxY = Math.Max(maxY, b.MaxY);
            }

            List<ShapePolygon> anchors = new List<ShapePolygon>();
            foreach (ShapePolygon polygon in placed)
            {
                ShapeBounds b = polygon.Bounds();
                bool right = b.MaxX >= maxX - 1e-6;
                bool top = b.MaxY >= maxY - 1e-6;
                bool left = b.MinX <= minX + 1e-6;
                bool bottom = b.MinY <= minY + 1e-6;
                if ((mode == 1 && right) ||
                    (mode == 2 && top) ||
                    (mode == 3 && (right || top)) ||
                    (mode == 4 && (left || bottom || right || top)))
                {
                    anchors.Add(polygon);
                }
            }

            if (anchors.Count == 0)
            {
                anchors.AddRange(placed);
            }
            return anchors;
        }

        private static List<ShapePoint> GroupCandidateOffsets(List<ShapePolygon> placed, List<ShapePolygon> moving)
        {
            List<ShapePoint> nfpCandidates = NfpGroupCandidateOffsets(placed, moving, GroupCandidateLimit);
            if (nfpCandidates.Count > 0)
            {
                return nfpCandidates;
            }

            List<ShapePoint> candidates = new List<ShapePoint>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            ShapeBounds mb = BoundsOf(moving);
            List<ShapePoint> movingAnchors = LimitAnchors(TemplateAnchorPoints(moving), 32);
            foreach (ShapePolygon existing in placed)
            {
                ShapeBounds b = existing.Bounds();
                AddOffset(candidates, seen, b.MaxX - mb.MinX, b.MinY - mb.MinY);
                AddOffset(candidates, seen, b.MinX - mb.MaxX, b.MinY - mb.MinY);
                AddOffset(candidates, seen, b.MinX - mb.MinX, b.MaxY - mb.MinY);
                AddOffset(candidates, seen, b.MinX - mb.MinX, b.MinY - mb.MaxY);
                AddOffset(candidates, seen, b.MaxX + Gap - mb.MinX, b.MinY - mb.MinY);
                AddOffset(candidates, seen, b.MinX - Gap - mb.MaxX, b.MinY - mb.MinY);
                AddOffset(candidates, seen, b.MinX - mb.MinX, b.MaxY + Gap - mb.MinY);
                AddOffset(candidates, seen, b.MinX - mb.MinX, b.MinY - Gap - mb.MaxY);
                AddOffset(candidates, seen, b.MaxX - mb.Width, b.MinY - mb.MinY);
                AddOffset(candidates, seen, b.MinX - mb.MinX, b.MaxY - mb.Height);

                List<ShapePoint> fixedAnchors = LimitAnchors(HugAnchorPoints(existing), 16);
                foreach (ShapePoint fixedPoint in fixedAnchors)
                {
                    foreach (ShapePoint movingPoint in movingAnchors)
                    {
                        AddOffset(candidates, seen, fixedPoint.X - movingPoint.X, fixedPoint.Y - movingPoint.Y);
                        AddOffset(candidates, seen, fixedPoint.X - movingPoint.X + Gap, fixedPoint.Y - movingPoint.Y);
                        AddOffset(candidates, seen, fixedPoint.X - movingPoint.X - Gap, fixedPoint.Y - movingPoint.Y);
                        AddOffset(candidates, seen, fixedPoint.X - movingPoint.X, fixedPoint.Y - movingPoint.Y + Gap);
                        AddOffset(candidates, seen, fixedPoint.X - movingPoint.X, fixedPoint.Y - movingPoint.Y - Gap);
                        if (candidates.Count >= GroupCandidateLimit)
                        {
                            return candidates;
                        }
                    }
                }
                if (candidates.Count >= GroupCandidateLimit)
                {
                    return candidates;
                }
            }
            return candidates;
        }

        private static List<ShapePoint> NfpGroupCandidateOffsets(List<ShapePolygon> placed, List<ShapePolygon> moving, int limit)
        {
            List<ShapePoint> candidates = new List<ShapePoint>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            foreach (ShapePolygon fixedPolygon in placed)
            {
                foreach (ShapePolygon movingPolygon in moving)
                {
                    foreach (ShapePoint offset in PairNfpBoundaryOffsets(fixedPolygon, movingPolygon))
                    {
                        PumpUi(false);
                        List<ShapePolygon> moved = TranslatePolygons(moving, offset.X, offset.Y);
                        if (GroupPolygonsOverlap(moved, placed))
                        {
                            continue;
                        }
                        AddOffset(candidates, seen, offset.X, offset.Y);
                        if (candidates.Count >= limit)
                        {
                            SortGroupOffsets(candidates, placed, moving);
                            return candidates;
                        }
                    }
                }
            }
            SortGroupOffsets(candidates, placed, moving);
            return candidates;
        }

        private static void SortGroupOffsets(List<ShapePoint> candidates, List<ShapePolygon> placed, List<ShapePolygon> moving)
        {
            candidates.Sort(delegate (ShapePoint a, ShapePoint b)
            {
                double ascore = GroupOffsetScore(placed, moving, a);
                double bscore = GroupOffsetScore(placed, moving, b);
                return ascore.CompareTo(bscore);
            });
        }

        private static double GroupOffsetScore(List<ShapePolygon> placed, List<ShapePolygon> moving, ShapePoint offset)
        {
            List<ShapePolygon> moved = TranslatePolygons(moving, offset.X, offset.Y);
            List<ShapePolygon> all = new List<ShapePolygon>(placed);
            all.AddRange(moved);
            ShapeBounds bounds = BoundsOf(all);
            return bounds.Width * bounds.Height +
                Math.Max(bounds.Width, bounds.Height) * 80.0 +
                bounds.MinY * 800.0 +
                bounds.MinX * 450.0 -
                GroupContactReward(moved, placed) * 500.0 -
                GroupNearReward(moved, placed) * 260.0;
        }

        private static double PackedGroupScore(ShapeBounds bounds, double near, double contact, int mode)
        {
            double area = bounds.Width * bounds.Height;
            if (mode == 1)
            {
                return area + bounds.Height * 1350.0 + bounds.Width * 90.0 - near * 16000.0 - contact * 10000.0;
            }
            if (mode == 2)
            {
                return area + bounds.Width * 1350.0 + bounds.Height * 90.0 - near * 16000.0 - contact * 10000.0;
            }
            if (mode == 4)
            {
                return area + Math.Max(bounds.Width, bounds.Height) * 220.0 - near * 22000.0 - contact * 14000.0;
            }
            return area + Math.Abs(bounds.Width - bounds.Height) * 80.0 + Math.Max(bounds.Width, bounds.Height) * 240.0 - near * 20000.0 - contact * 12000.0;
        }

        private static List<ShapePolygon> TranslatePolygons(List<ShapePolygon> polygons, double x, double y)
        {
            List<ShapePolygon> moved = new List<ShapePolygon>();
            foreach (ShapePolygon polygon in polygons)
            {
                moved.Add(polygon.Translate(x, y));
            }
            return moved;
        }

        private static List<ShapePolygon> RotateAndNormalize(List<ShapePolygon> polygons, int rotation)
        {
            return RotateAndNormalize(polygons, (double)rotation);
        }

        private static ShapePolygon RotatePolygonNormalize(ShapePolygon polygon, double rotation)
        {
            return RotateAndNormalize(new List<ShapePolygon> { polygon }, rotation)[0];
        }

        private static List<ShapePolygon> RotateAndNormalize(List<ShapePolygon> polygons, double rotation)
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

        private static List<double> TemplatePlacementAngles(List<ShapePolygon> polygons)
        {
            List<double> angles = new List<double>();
            if (IsTriangleTemplate(polygons))
            {
                List<double> minAngles = MinBoundingBoxAngles(polygons);
                double baseAngle = minAngles.Count > 0 ? minAngles[0] : 0.0;
                AddAngle(angles, baseAngle);
                AddAngle(angles, baseAngle + 90.0);
                return angles;
            }
            AddAngles(angles, new double[] { 0.0, 90.0, 180.0, 270.0 });
            foreach (double angle in MinBoundingBoxAngles(polygons))
            {
                AddAngle(angles, angle);
                AddAngle(angles, angle + 90.0);
                AddAngle(angles, angle + 180.0);
                AddAngle(angles, angle + 270.0);
            }
            return angles;
        }

        private static bool IsTriangleTemplate(List<ShapePolygon> polygons)
        {
            if (polygons == null || polygons.Count == 0)
            {
                return false;
            }
            foreach (ShapePolygon polygon in polygons)
            {
                if (polygon == null || polygon.Points == null || polygon.Points.Count != 3)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool TemplateCanFitBoardInUsefulOrientation(ShapeNestingInput input, List<ShapePolygon> polygons)
        {
            foreach (double angle in TemplatePlacementAngles(polygons))
            {
                ShapeBounds bounds = BoundsOf(RotateAndNormalize(polygons, angle));
                if (bounds.Width <= input.BoardWidth + 1e-6 && bounds.Height <= input.BoardHeight + 1e-6)
                {
                    return true;
                }
            }
            return false;
        }

        private static List<double> MinBoundingBoxAngles(List<ShapePolygon> polygons)
        {
            List<double> best = new List<double>();
            double bestArea = double.PositiveInfinity;
            foreach (double edgeAngle in EdgeAngles(polygons))
            {
                double angle = NormalizeAngle(-edgeAngle);
                ShapeBounds bounds = BoundsOf(RotateAndNormalize(polygons, angle));
                double area = bounds.Width * bounds.Height;
                if (area < bestArea - 1.0)
                {
                    bestArea = area;
                    best.Clear();
                    AddAngle(best, angle);
                }
                else if (Math.Abs(area - bestArea) <= 1.0)
                {
                    AddAngle(best, angle);
                }
            }
            if (best.Count == 0)
            {
                AddAngle(best, 0.0);
            }
            return best;
        }

        private static List<double> EdgeAngles(List<ShapePolygon> polygons)
        {
            List<double> angles = new List<double>();
            foreach (ShapePolygon polygon in polygons)
            {
                for (int i = 0; i < polygon.Points.Count; i++)
                {
                    ShapePoint a = polygon.Points[i];
                    ShapePoint b = polygon.Points[(i + 1) % polygon.Points.Count];
                    double dx = b.X - a.X;
                    double dy = b.Y - a.Y;
                    double length = Math.Sqrt(dx * dx + dy * dy);
                    if (length <= 1e-6)
                    {
                        continue;
                    }
                    double angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                    AddAngle(angles, angle);
                }
            }
            return angles;
        }

        private static void AddAngles(List<double> angles, double[] values)
        {
            foreach (double value in values)
            {
                AddAngle(angles, value);
            }
        }

        private static void AddAngle(List<double> angles, double value)
        {
            double normalized = NormalizeAngle(value);
            foreach (double existing in angles)
            {
                double diff = Math.Abs(NormalizeAngle(existing - normalized));
                diff = Math.Min(diff, 360.0 - diff);
                if (diff <= 0.01)
                {
                    return;
                }
            }
            angles.Add(normalized);
        }

        private static double NormalizeAngle(double value)
        {
            double normalized = value % 360.0;
            if (normalized < 0.0)
            {
                normalized += 360.0;
            }
            return normalized;
        }

        private static bool IsAxisQuarterTurn(double value)
        {
            double normalized = NormalizeAngle(value);
            foreach (int rotation in Rotations)
            {
                double diff = Math.Abs(normalized - rotation);
                diff = Math.Min(diff, 360.0 - diff);
                if (diff <= 0.01)
                {
                    return true;
                }
            }
            return false;
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

        private static double GroupNearReward(List<ShapePolygon> moved, List<ShapePolygon> placed)
        {
            double reward = 0.0;
            foreach (ShapePolygon a in moved)
            {
                foreach (ShapePolygon b in placed)
                {
                    reward += ShapeNearReward(a, b, 80.0);
                }
            }
            return reward;
        }

        private static double GroupContactReward(List<ShapePolygon> moved, List<ShapePolygon> placed)
        {
            double reward = 0.0;
            foreach (ShapePolygon a in moved)
            {
                ShapeBounds ab = a.Bounds();
                foreach (ShapePolygon b in placed)
                {
                    reward += BoundsContact(ab, b.Bounds(), 8.0);
                }
            }
            return reward;
        }

        private static double BoundsOverlapArea(ShapeBounds a, ShapeBounds b)
        {
            double width = Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX);
            double height = Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
            if (width <= 0.0 || height <= 0.0)
            {
                return 0.0;
            }
            return width * height;
        }

        private static List<ShapePoint> LimitAnchors(List<ShapePoint> anchors, int limit)
        {
            if (anchors.Count <= limit)
            {
                return anchors;
            }
            List<ShapePoint> selected = new List<ShapePoint>();
            for (int i = 0; i < limit; i++)
            {
                int index = (int)Math.Floor(i * anchors.Count / (double)limit);
                selected.Add(anchors[index]);
            }
            return selected;
        }

        private static void AddOffset(List<ShapePoint> points, Dictionary<string, bool> seen, double x, double y)
        {
            string key = ((int)Math.Round(x * 100.0)).ToString(CultureInfo.InvariantCulture) + "," + ((int)Math.Round(y * 100.0)).ToString(CultureInfo.InvariantCulture);
            if (seen.ContainsKey(key))
            {
                return;
            }
            seen.Add(key, true);
            points.Add(new ShapePoint(x, y));
        }

        private static int[] TemplateRotations(V4Strategy strategy, int step)
        {
            if (step % 3 == 0)
            {
                return new int[] { strategy.MainRotation, NormalizeRotation(strategy.MainRotation + 90), NormalizeRotation(strategy.MainRotation + 270) };
            }
            if (step % 3 == 1)
            {
                return new int[] { NormalizeRotation(strategy.MainRotation + 90), NormalizeRotation(strategy.MainRotation + 180), NormalizeRotation(strategy.MainRotation + 270) };
            }
            return new int[] { strategy.MainRotation, NormalizeRotation(strategy.MainRotation + 180) };
        }

        private static List<ShapePoint> LimitTemplatePoints(List<ShapePoint> points, V4Strategy strategy)
        {
            points.Sort(delegate (ShapePoint a, ShapePoint b)
            {
                int cmp = a.X.CompareTo(b.X);
                return cmp != 0 ? cmp : a.Y.CompareTo(b.Y);
            });
            if (points.Count <= TemplateCandidateLimit)
            {
                return points;
            }
            List<ShapePoint> selected = new List<ShapePoint>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            int buckets = Math.Min(24, points.Count);
            for (int bucket = 0; bucket < buckets && selected.Count < TemplateCandidateLimit; bucket++)
            {
                int start = (int)Math.Floor(points.Count * bucket / (double)buckets);
                int end = (int)Math.Floor(points.Count * (bucket + 1) / (double)buckets);
                int take = Math.Min(end, start + Math.Max(1, TemplateCandidateLimit / buckets));
                for (int i = start; i < take && i < points.Count && selected.Count < TemplateCandidateLimit; i++)
                {
                    AddSelectedTemplatePoint(selected, seen, points[i]);
                }
            }
            for (int i = 0; i < points.Count && selected.Count < TemplateCandidateLimit; i++)
            {
                AddSelectedTemplatePoint(selected, seen, points[i]);
            }
            selected.Sort(delegate (ShapePoint a, ShapePoint b)
            {
                int cmp = a.X.CompareTo(b.X);
                return cmp != 0 ? cmp : a.Y.CompareTo(b.Y);
            });
            return selected;
        }

        private static void AddSelectedTemplatePoint(List<ShapePoint> selected, Dictionary<string, bool> seen, ShapePoint point)
        {
            string key = ((int)Math.Round(point.X * 100.0)).ToString(CultureInfo.InvariantCulture) + "," + ((int)Math.Round(point.Y * 100.0)).ToString(CultureInfo.InvariantCulture);
            if (seen.ContainsKey(key))
            {
                return;
            }
            seen.Add(key, true);
            selected.Add(point);
        }

        private static void AddRawCandidate(List<ShapePoint> points, Dictionary<string, bool> seen, double x, double y)
        {
            if (x < -1e-6 || y < -1e-6)
            {
                return;
            }
            string key = ((int)Math.Round(x * 100.0)).ToString(CultureInfo.InvariantCulture) + "," + ((int)Math.Round(y * 100.0)).ToString(CultureInfo.InvariantCulture);
            if (seen.ContainsKey(key))
            {
                return;
            }
            seen.Add(key, true);
            points.Add(new ShapePoint(x, y));
        }

        private static double TemplateNearReward(ShapePolygon candidate, List<ShapePolygon> placed)
        {
            double reward = 0.0;
            foreach (ShapePolygon existing in placed)
            {
                reward += ShapeNearReward(candidate, existing, HugNearDistance);
            }
            return reward;
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

        private static double EdgeNearReward(ShapePolygon a, ShapePolygon b, double maxDistance)
        {
            double reward = 0.0;
            double best = double.PositiveInfinity;
            for (int i = 0; i < a.Points.Count; i++)
            {
                ShapePoint a1 = a.Points[i];
                ShapePoint a2 = a.Points[(i + 1) % a.Points.Count];
                for (int j = 0; j < b.Points.Count; j++)
                {
                    ShapePoint b1 = b.Points[j];
                    ShapePoint b2 = b.Points[(j + 1) % b.Points.Count];
                    double distance = SegmentDistance(a1, a2, b1, b2);
                    if (distance < best)
                    {
                        best = distance;
                    }
                    if (distance <= maxDistance)
                    {
                        reward += maxDistance - distance;
                    }
                }
            }
            if (best > maxDistance)
            {
                return 0.0;
            }
            return reward + (maxDistance - best) * 12.0;
        }

        private static double MinEdgeDistance(ShapePolygon a, ShapePolygon b)
        {
            double best = double.PositiveInfinity;
            for (int i = 0; i < a.Points.Count; i++)
            {
                ShapePoint a1 = a.Points[i];
                ShapePoint a2 = a.Points[(i + 1) % a.Points.Count];
                for (int j = 0; j < b.Points.Count; j++)
                {
                    ShapePoint b1 = b.Points[j];
                    ShapePoint b2 = b.Points[(j + 1) % b.Points.Count];
                    best = Math.Min(best, SegmentDistance(a1, a2, b1, b2));
                }
            }
            return best;
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

        private static double TemplateContactReward(ShapePolygon candidate, List<ShapePolygon> placed)
        {
            double reward = 0.0;
            ShapeBounds cb = candidate.Bounds();
            foreach (ShapePolygon existing in placed)
            {
                reward += BoundsContact(cb, existing.Bounds(), 8.0);
            }
            return reward;
        }

        private static double TemplateGrowthScore(ShapeBounds bounds, double near, double contact, V4Strategy strategy, int step)
        {
            double longSide = Math.Max(bounds.Width, bounds.Height);
            double shortSide = Math.Min(bounds.Width, bounds.Height);
            double aspect = longSide / Math.Max(1.0, shortSide);
            double stretch = strategy.TemplateAreaWeight * bounds.Width * bounds.Height;
            double modeBias = 0.0;
            if (strategy.ExpansionMode == 1)
            {
                modeBias = bounds.Height * 95.0 - bounds.Width * 8.0;
            }
            else if (strategy.ExpansionMode == 2)
            {
                modeBias = bounds.Width * 95.0 - bounds.Height * 8.0;
            }
            return bounds.Width * bounds.Height +
                longSide * 140.0 +
                shortSide * 45.0 +
                aspect * 120.0 +
                step * 20.0 +
                modeBias +
                stretch -
                near * 1600.0 -
                contact * 900.0;
        }

        private static List<ShapePoint> AddBoardHugCandidatePoints(List<ShapePoint> points, Dictionary<string, bool> seen, List<ShapePlacement> board, V4Template template)
        {
            if (board.Count == 0)
            {
                return points;
            }
            List<ShapePoint> movingAnchors = TemplateAnchorPoints(template);
            foreach (ShapePlacement existing in board)
            {
                List<ShapePoint> existingAnchors = TemplateAnchorPoints(ActualPolygons(existing));
                foreach (ShapePoint fixedPoint in existingAnchors)
                {
                    foreach (ShapePoint movingPoint in movingAnchors)
                    {
                        AddCandidate(points, seen, fixedPoint.X - movingPoint.X + Gap, fixedPoint.Y - movingPoint.Y);
                        AddCandidate(points, seen, fixedPoint.X - movingPoint.X - Gap, fixedPoint.Y - movingPoint.Y);
                        AddCandidate(points, seen, fixedPoint.X - movingPoint.X, fixedPoint.Y - movingPoint.Y + Gap);
                        AddCandidate(points, seen, fixedPoint.X - movingPoint.X, fixedPoint.Y - movingPoint.Y - Gap);
                    }
                }
            }
            return points;
        }

        private static List<ShapePoint> TemplateAnchorPoints(V4Template template)
        {
            return TemplateAnchorPoints(template.Polygons);
        }

        private static List<ShapePoint> TemplateAnchorPoints(List<ShapePolygon> polygons)
        {
            List<ShapePoint> anchors = new List<ShapePoint>();
            foreach (ShapePolygon polygon in polygons)
            {
                anchors.AddRange(HugAnchorPoints(polygon));
            }
            return anchors;
        }

        private static List<ShapePoint> HugAnchorPoints(ShapePolygon part)
        {
            ShapeBounds b = part.Bounds();
            List<ShapePoint> anchors = new List<ShapePoint>();
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

        private static int NormalizeRotation(int rotation)
        {
            int value = rotation % 360;
            if (value < 0)
            {
                value += 360;
            }
            return value;
        }

        private static V4Template HugTemplate(ShapePolygon source, int count, V4Strategy strategy)
        {
            List<ShapePolygon> polygons = new List<ShapePolygon>();
            polygons.Add(source.RotateNormalize(strategy.MainRotation));
            while (polygons.Count < count && polygons.Count < MaxPatternRepeat)
            {
                ShapePolygon best = null;
                double bestScore = double.PositiveInfinity;
                int step = polygons.Count;
                foreach (int rotation in TemplateRotations(strategy, step))
                {
                    ShapePolygon moving = source.RotateNormalize(rotation);
                    foreach (ShapePoint point in TemplateHugOffsets(polygons, moving, strategy))
                    {
                        ShapePolygon candidate = moving.Translate(point.X, point.Y);
                        if (Overlaps(candidate, polygons))
                        {
                            continue;
                        }
                        List<ShapePolygon> all = new List<ShapePolygon>(polygons);
                        all.Add(candidate);
                        ShapeBounds bounds = BoundsOf(all);
                        double near = TemplateNearReward(candidate, polygons);
                        double contact = TemplateContactReward(candidate, polygons);
                        double score = TemplateGrowthScore(bounds, near, contact, strategy, step);
                        if (score < bestScore)
                        {
                            bestScore = score;
                            best = candidate;
                        }
                    }
                }
                if (best == null)
                {
                    break;
                }
                polygons.Add(best);
            }
            return NormalizeTemplate(polygons);
        }

        private static V4Template TightHugTemplate(ShapeNestingInput input, ShapePolygon source, int count, V4Strategy strategy)
        {
            if (count <= 0)
            {
                return null;
            }

            if (count == 1)
            {
                return SingleTemplate(source, strategy.MainRotation);
            }

            V4Template pair = BuildPairHugTemplate(input, source, strategy);
            if (pair == null)
            {
                return null;
            }

            if (count == 2)
            {
                return pair;
            }

            return PairUnitRepeatTemplate(input, pair, count, 0, true) ?? PairUnitRepeatTemplate(input, pair, count, 90, true) ?? PairUnitRepeatTemplate(input, pair, count, 0, false) ?? PairUnitRepeatTemplate(input, pair, count, 90, false) ?? pair;
        }

        private static V4Template SingleTemplate(ShapePolygon source, int rotation)
        {
            ShapePolygon polygon = source.RotateNormalize(rotation);
            return NormalizeTemplate(new List<ShapePolygon> { polygon });
        }

        private static V4Template RowTemplate(ShapePolygon source, int count, int rotation, bool horizontal)
        {
            List<ShapePolygon> polygons = new List<ShapePolygon>();
            ShapePolygon p = source.RotateNormalize(rotation);
            ShapeBounds b = p.Bounds();
            for (int i = 0; i < count; i++)
            {
                double x = horizontal ? i * (b.Width + Gap) : 0.0;
                double y = horizontal ? 0.0 : i * (b.Height + Gap);
                polygons.Add(p.Translate(x, y));
            }
            return NormalizeTemplate(polygons);
        }

        private static V4Template StaggerTemplate(ShapePolygon source, int count, int rotation, bool horizontal)
        {
            List<ShapePolygon> polygons = new List<ShapePolygon>();
            ShapePolygon p = source.RotateNormalize(rotation);
            ShapeBounds b = p.Bounds();
            for (int i = 0; i < count; i++)
            {
                double x = horizontal ? i * (b.Width * 0.72 + Gap) : (i % 2) * (b.Width * 0.35);
                double y = horizontal ? (i % 2) * (b.Height * 0.45) : i * (b.Height * 0.72 + Gap);
                polygons.Add(p.Translate(x, y));
            }
            return NormalizeTemplate(RemoveOverlapping(polygons));
        }

        private static V4Template CompactTemplate(ShapePolygon source, int count, int rotation)
        {
            List<ShapePolygon> polygons = new List<ShapePolygon>();
            ShapePolygon p = source.RotateNormalize(rotation);
            polygons.Add(p);
            while (polygons.Count < count && polygons.Count < MaxPatternRepeat)
            {
                ShapePolygon best = null;
                double bestScore = double.PositiveInfinity;
                foreach (int r in Rotations)
                {
                    ShapePolygon moving = source.RotateNormalize(r);
                    foreach (ShapePoint point in TemplateCandidateOffsets(polygons, moving))
                    {
                        ShapePolygon candidate = moving.Translate(point.X, point.Y);
                        if (Overlaps(candidate, polygons))
                        {
                            continue;
                        }
                        List<ShapePolygon> all = new List<ShapePolygon>(polygons);
                        all.Add(candidate);
                        ShapeBounds bounds = BoundsOf(all);
                        double score = bounds.Width * bounds.Height + Math.Max(bounds.Width, bounds.Height) * 180.0;
                        if (score < bestScore)
                        {
                            bestScore = score;
                            best = candidate;
                        }
                    }
                }
                if (best == null)
                {
                    break;
                }
                polygons.Add(best);
            }
            return NormalizeTemplate(polygons);
        }

        private static List<ShapePolygon> RemoveOverlapping(List<ShapePolygon> polygons)
        {
            List<ShapePolygon> clean = new List<ShapePolygon>();
            foreach (ShapePolygon polygon in polygons)
            {
                if (!Overlaps(polygon, clean))
                {
                    clean.Add(polygon);
                }
            }
            return clean.Count == 0 ? polygons : clean;
        }

        private static List<ShapePoint> TemplateCandidateOffsets(List<ShapePolygon> placed, ShapePolygon moving)
        {
            List<ShapePoint> points = new List<ShapePoint>();
            ShapeBounds mb = moving.Bounds();
            foreach (ShapePolygon existing in placed)
            {
                ShapeBounds b = existing.Bounds();
                points.Add(new ShapePoint(b.MaxX + Gap - mb.MinX, b.MinY - mb.MinY));
                points.Add(new ShapePoint(b.MinX - mb.MinX, b.MaxY + Gap - mb.MinY));
                points.Add(new ShapePoint(Math.Max(0.0, b.MaxX - mb.Width), b.MinY - mb.MinY));
                points.Add(new ShapePoint(b.MinX - mb.MinX, Math.Max(0.0, b.MaxY - mb.Height)));
            }
            return points;
        }

        private static List<ShapePoint> TemplateHugOffsets(List<ShapePolygon> placed, ShapePolygon moving, V4Strategy strategy)
        {
            List<ShapePoint> points = new List<ShapePoint>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            ShapeBounds mb = moving.Bounds();
            foreach (ShapePolygon existing in placed)
            {
                ShapeBounds b = existing.Bounds();
                AddRawCandidate(points, seen, b.MaxX + Gap - mb.MinX, b.MinY - mb.MinY);
                AddRawCandidate(points, seen, b.MinX - Gap - mb.MaxX, b.MinY - mb.MinY);
                AddRawCandidate(points, seen, b.MinX - mb.MinX, b.MaxY + Gap - mb.MinY);
                AddRawCandidate(points, seen, b.MinX - mb.MinX, b.MinY - Gap - mb.MaxY);
                AddRawCandidate(points, seen, b.MaxX - mb.Width, b.MinY - mb.MinY);
                AddRawCandidate(points, seen, b.MinX - mb.MinX, b.MaxY - mb.Height);

                List<ShapePoint> fixedAnchors = HugAnchorPoints(existing);
                List<ShapePoint> movingAnchors = HugAnchorPoints(moving);
                foreach (ShapePoint fixedPoint in fixedAnchors)
                {
                    foreach (ShapePoint movingPoint in movingAnchors)
                    {
                        AddRawCandidate(points, seen, fixedPoint.X - movingPoint.X + Gap, fixedPoint.Y - movingPoint.Y);
                        AddRawCandidate(points, seen, fixedPoint.X - movingPoint.X - Gap, fixedPoint.Y - movingPoint.Y);
                        AddRawCandidate(points, seen, fixedPoint.X - movingPoint.X, fixedPoint.Y - movingPoint.Y + Gap);
                        AddRawCandidate(points, seen, fixedPoint.X - movingPoint.X, fixedPoint.Y - movingPoint.Y - Gap);
                        if (points.Count >= TemplateCandidateLimit * 3)
                        {
                            return LimitTemplatePoints(points, strategy);
                        }
                    }
                }
            }
            return LimitTemplatePoints(points, strategy);
        }

        private static V4Template NormalizeTemplate(List<ShapePolygon> polygons)
        {
            ShapeBounds bounds = BoundsOf(polygons);
            List<ShapePolygon> normalized = new List<ShapePolygon>();
            foreach (ShapePolygon polygon in polygons)
            {
                normalized.Add(polygon.Translate(-bounds.MinX, -bounds.MinY));
            }
            return new V4Template { Polygons = normalized, Bounds = BoundsOf(normalized), Count = normalized.Count };
        }

        private static void AddTemplate(List<V4Template> templates, V4Template template)
        {
            if (template == null || template.Count <= 0)
            {
                return;
            }
            if (InternalTemplateOverlap(template.Polygons))
            {
                return;
            }
            string key = TemplateKey(template);
            foreach (V4Template existing in templates)
            {
                if (TemplateKey(existing) == key)
                {
                    return;
                }
            }
            templates.Add(template);
        }

        private static void AddTemplateAllowSameShape(List<V4Template> templates, V4Template template)
        {
            if (template == null || template.Count <= 0)
            {
                return;
            }
            if (InternalTemplateOverlap(template.Polygons))
            {
                return;
            }
            templates.Add(template);
        }

        private static bool InternalTemplateOverlap(List<ShapePolygon> polygons)
        {
            for (int i = 0; i < polygons.Count; i++)
            {
                ShapeBounds aBounds = polygons[i].Bounds();
                for (int j = i + 1; j < polygons.Count; j++)
                {
                    ShapeBounds bBounds = polygons[j].Bounds();
                    if (ShapeCollision.BoundsOverlap(aBounds, bBounds) &&
                        ShapeCollision.Intersects(polygons[i], aBounds, polygons[j], bBounds))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static List<ShapePoint> CandidatePoints(ShapeNestingInput input, List<ShapePlacement> board, V4Template template, bool fillMode)
        {
            long timingStart = V4Timing.Start();
            try
            {
                List<ShapePoint> points = new List<ShapePoint>();
                Dictionary<string, bool> seen = new Dictionary<string, bool>();
                if (board.Count == 0)
                {
                    AddCandidate(points, seen, 0.0, 0.0);
                    _timing.CandidatePoints += points.Count;
                    return points;
                }

                double usedX = UsedX(board);
                double usedY = UsedY(board);
                List<V4FreeRegion> freeRegions = FindFreeRegions(input, board, template);
                AddNfpCandidatePoints(points, seen, input, board, template);
                AddFreeRegionCandidatePoints(points, seen, input, board, template, freeRegions);
                if (points.Count == 0)
                {
                    AddCandidate(points, seen, 0.0, 0.0);
                    AddCandidate(points, seen, Math.Max(0.0, usedX + Gap), 0.0);
                    AddCandidate(points, seen, 0.0, Math.Max(0.0, usedY + Gap));
                    if (template.ShelfLayout)
                    {
                        AddShelfCandidatePoints(points, seen, board, template);
                    }
                    foreach (ShapePlacement existing in NfpAnchorPlacements(board))
                    {
                        AddCandidate(points, seen, existing.Bounds.MaxX + Gap, existing.Bounds.MinY);
                        AddCandidate(points, seen, existing.Bounds.MinX, existing.Bounds.MaxY + Gap);
                    }
                    if (points.Count < (fillMode ? CoarseFillCandidateLimit : CoarseCandidateLimit))
                    {
                        AddBoardHugCandidatePoints(points, seen, board, template);
                    }
                }
                points = LimitPointsByFreeRegions(input, board, template, points, freeRegions, fillMode ? FillCandidateLimit : CandidateLimit);
                List<ShapePoint> filtered = CoarseFilterCandidatePoints(input, board, template, points, fillMode, freeRegions);
                _timing.CandidatePoints += filtered.Count;
                return filtered;
            }
            finally
            {
                _timing.CandidateTicks += V4Timing.Elapsed(timingStart);
                _timing.CandidateCalls++;
            }
        }

        private static void AddNfpCandidatePoints(List<ShapePoint> points, Dictionary<string, bool> seen, ShapeNestingInput input, List<ShapePlacement> board, V4Template template)
        {
            long timingStart = V4Timing.Start();
            try
            {
            if (board.Count == 0 || template == null || template.Polygons == null || template.Polygons.Count == 0)
            {
                return;
            }
            if (TemplatePointCount(template) > NfpPolygonPointLimit)
            {
                return;
            }

            List<ShapePlacement> anchors = NfpAnchorPlacements(board);
            ShapePartInstance probePart = CreateNfpProbePart(template);
            int added = 0;
            foreach (ShapePlacement existing in anchors)
            {
                if (added >= NfpCandidateLimit)
                {
                    break;
                }
                V4Template existingTemplate = TemplateFromPlacement(existing);
                if (TemplatePointCount(existingTemplate) > NfpPolygonPointLimit)
                {
                    continue;
                }
                foreach (ShapePoint candidate in NfpBoundaryCandidates(existingTemplate, template, existing.X, existing.Y))
                {
                    if (added >= NfpCandidateLimit)
                    {
                        break;
                    }
                    ShapePlacement placement = MakePlacement(probePart, template, 0, candidate.X, candidate.Y);
                    if (!FitsBoard(input, placement.Bounds) || HitsAny(placement, board))
                    {
                        continue;
                    }
                    AddCandidate(points, seen, candidate.X, candidate.Y);
                    AddCandidate(points, seen, candidate.X, 0.0);
                    AddCandidate(points, seen, 0.0, candidate.Y);
                    added++;
                }
            }
            }
            finally
            {
                _timing.NfpTicks += V4Timing.Elapsed(timingStart);
                _timing.NfpCalls++;
            }
        }

        private static void AddFreeRegionCandidatePoints(List<ShapePoint> points, Dictionary<string, bool> seen, ShapeNestingInput input, List<ShapePlacement> board, V4Template template, List<V4FreeRegion> regions)
        {
            if (board.Count == 0 || template == null)
            {
                return;
            }

            int added = 0;
            foreach (V4FreeRegion region in regions)
            {
                if (added >= FreeRegionCandidateLimit)
                {
                    break;
                }
                AddFreeRegionCandidate(points, seen, input, template, region.Bounds.MinX - template.Bounds.MinX, region.Bounds.MinY - template.Bounds.MinY, ref added);
                AddFreeRegionCandidate(points, seen, input, template, region.Bounds.MaxX - template.Bounds.MaxX, region.Bounds.MinY - template.Bounds.MinY, ref added);
                AddFreeRegionCandidate(points, seen, input, template, region.Bounds.MinX - template.Bounds.MinX, region.Bounds.MaxY - template.Bounds.MaxY, ref added);

                foreach (ShapePoint point in region.CornerPoints)
                {
                    if (added >= FreeRegionCandidateLimit)
                    {
                        break;
                    }
                    AddFreeRegionCandidate(points, seen, input, template, point.X - template.Bounds.MinX, point.Y - template.Bounds.MinY, ref added);
                    AddFreeRegionCandidate(points, seen, input, template, point.X - template.Bounds.MaxX, point.Y - template.Bounds.MinY, ref added);
                    AddFreeRegionCandidate(points, seen, input, template, point.X - template.Bounds.MinX, point.Y - template.Bounds.MaxY, ref added);
                }
            }
        }

        private static void AddFreeRegionCandidate(List<ShapePoint> points, Dictionary<string, bool> seen, ShapeNestingInput input, V4Template template, double x, double y, ref int added)
        {
            ShapeBounds bounds = TranslateBounds(template.Bounds, x, y);
            if (!FitsBoard(input, bounds))
            {
                return;
            }
            int before = points.Count;
            AddCandidate(points, seen, x, y);
            if (points.Count > before)
            {
                added++;
            }
        }

        private static List<ShapePoint> LimitPointsByFreeRegions(ShapeNestingInput input, List<ShapePlacement> board, V4Template template, List<ShapePoint> points, List<V4FreeRegion> regions, int limit)
        {
            if (points.Count <= limit || regions == null || regions.Count == 0)
            {
                return LimitPoints(points, limit);
            }

            double usedX = UsedX(board);
            List<V4ScoredPoint> scored = new List<V4ScoredPoint>();
            foreach (ShapePoint point in points)
            {
                ShapeBounds bounds = TranslateBounds(template.Bounds, point.X, point.Y);
                if (!FitsBoard(input, bounds))
                {
                    continue;
                }

                double addedX = Math.Max(0.0, bounds.MaxX - usedX);
                double affinity = FreeRegionAffinity(bounds, regions);
                double score = bounds.MinY * 900000.0 +
                    bounds.MinX * 240000.0 +
                    bounds.MaxY * 1800.0 +
                    addedX * 360000.0 -
                    affinity * input.BoardWidth * input.BoardHeight * 2.0;
                scored.Add(new V4ScoredPoint { Point = point, Score = score });
            }

            if (scored.Count == 0)
            {
                return LimitPoints(points, limit);
            }

            scored.Sort(delegate (V4ScoredPoint a, V4ScoredPoint b)
            {
                int cmp = a.Score.CompareTo(b.Score);
                if (cmp != 0)
                {
                    return cmp;
                }
                cmp = a.Point.Y.CompareTo(b.Point.Y);
                return cmp != 0 ? cmp : a.Point.X.CompareTo(b.Point.X);
            });

            List<ShapePoint> selected = new List<ShapePoint>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            for (int i = 0; i < scored.Count && selected.Count < limit; i++)
            {
                AddSelectedTemplatePoint(selected, seen, scored[i].Point);
            }

            AddSelectedTemplatePoint(selected, seen, new ShapePoint(0.0, 0.0));
            if (board.Count > 0)
            {
                AddSelectedTemplatePoint(selected, seen, new ShapePoint(Math.Max(0.0, usedX + Gap), 0.0));
                AddSelectedTemplatePoint(selected, seen, new ShapePoint(0.0, Math.Max(0.0, UsedY(board) + Gap)));
            }

            selected.Sort(delegate (ShapePoint a, ShapePoint b)
            {
                int cmp = a.Y.CompareTo(b.Y);
                return cmp != 0 ? cmp : a.X.CompareTo(b.X);
            });
            return selected;
        }

        private static List<ShapePoint> FilterCandidatePointsByFreeRegions(ShapeNestingInput input, List<ShapePlacement> board, V4Template template, List<ShapePoint> points)
        {
            if (board.Count == 0 || points.Count <= 3)
            {
                return points;
            }

            List<V4FreeRegion> regions = FindFreeRegions(input, board, template);
            if (regions.Count == 0)
            {
                return points;
            }

            List<ShapePoint> kept = new List<ShapePoint>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            foreach (ShapePoint point in points)
            {
                ShapeBounds bounds = TranslateBounds(template.Bounds, point.X, point.Y);
                if (TemplateBoundsCanUseFreeRegion(bounds, regions) || bounds.MaxX <= UsedX(board) + template.Bounds.Width * 0.25)
                {
                    AddSelectedTemplatePoint(kept, seen, point);
                }
            }
            return kept.Count == 0 ? points : kept;
        }

        private static bool TemplateBoundsCanUseFreeRegion(ShapeBounds bounds, List<V4FreeRegion> regions)
        {
            foreach (V4FreeRegion region in regions)
            {
                if (bounds.MinX >= region.Bounds.MinX - Gap &&
                    bounds.MinY >= region.Bounds.MinY - Gap &&
                    bounds.MaxX <= region.Bounds.MaxX + Gap &&
                    bounds.MaxY <= region.Bounds.MaxY + Gap)
                {
                    return true;
                }
                double overlap = BoundsOverlapArea(bounds, region.Bounds);
                double area = Math.Max(1.0, bounds.Width * bounds.Height);
                if (overlap / area >= 0.72)
                {
                    return true;
                }
            }
            return false;
        }

        private static double FreeRegionAffinity(ShapeBounds bounds, List<V4FreeRegion> regions)
        {
            if (regions == null || regions.Count == 0)
            {
                return 0.0;
            }

            double boundsArea = Math.Max(1.0, bounds.Width * bounds.Height);
            double best = 0.0;
            foreach (V4FreeRegion region in regions)
            {
                double overlap = BoundsOverlapArea(bounds, region.Bounds);
                if (overlap <= 0.0)
                {
                    continue;
                }

                double overlapRatio = Math.Min(1.0, overlap / boundsArea);
                double regionArea = Math.Max(1.0, region.Area);
                double useRatio = Math.Min(1.0, boundsArea / regionArea);
                double insideBonus =
                    bounds.MinX >= region.Bounds.MinX - Gap &&
                    bounds.MinY >= region.Bounds.MinY - Gap &&
                    bounds.MaxX <= region.Bounds.MaxX + Gap &&
                    bounds.MaxY <= region.Bounds.MaxY + Gap ? 1.0 : 0.0;
                double lowerLeftBias = 1.0 / (1.0 + region.Bounds.MinX * 0.001 + region.Bounds.MinY * 0.002);
                double score = overlapRatio * 1.2 + insideBonus * 1.35 + useRatio * 0.35 + lowerLeftBias * 0.15;
                if (score > best)
                {
                    best = score;
                }
            }
            return best;
        }

        private static List<V4FreeRegion> FindFreeRegions(ShapeNestingInput input, List<ShapePlacement> board, V4Template template)
        {
            long timingStart = V4Timing.Start();
            try
            {
            List<V4FreeRegion> regions = new List<V4FreeRegion>();
            string cacheKey = FreeRegionCacheKey(input, board, template);
            List<V4FreeRegion> cached;
            if (_freeRegionCache.TryGetValue(cacheKey, out cached))
            {
                _timing.FreeRegionsFound += cached.Count;
                return cached;
            }

            List<List<IntPoint>> occupied = BoardOccupiedPaths(board);
            if (occupied.Count == 0)
            {
                regions.Add(MakeFreeRegion(BoardBounds(input)));
                _freeRegionCache[cacheKey] = regions;
                _timing.FreeRegionsFound += regions.Count;
                return regions;
            }

            List<IntPoint> boardPath = ToClipperPath(BoundsPolygon(BoardBounds(input)));
            List<List<IntPoint>> solution = new List<List<IntPoint>>();
            try
            {
                Clipper clipper = new Clipper();
                clipper.AddPath(boardPath, PolyType.ptSubject, true);
                clipper.AddPaths(occupied, PolyType.ptClip, true);
                clipper.Execute(ClipType.ctDifference, solution, PolyFillType.pftNonZero, PolyFillType.pftNonZero);
            }
            catch
            {
                _freeRegionCache[cacheKey] = regions;
                return regions;
            }

            double minWidth = Math.Max(FreeRegionMinEdge, Math.Min(template.Bounds.Width, template.Bounds.Height) * 0.35);
            double minHeight = Math.Max(FreeRegionMinEdge, Math.Min(template.Bounds.Width, template.Bounds.Height) * 0.35);
            foreach (List<IntPoint> path in solution)
            {
                if (path == null || path.Count < 3 || Math.Abs(Clipper.Area(path)) <= 1.0)
                {
                    continue;
                }
                V4FreeRegion region = MakeFreeRegion(path);
                if (region.Bounds.Width < minWidth || region.Bounds.Height < minHeight)
                {
                    continue;
                }
                regions.Add(region);
            }

            regions.Sort(delegate (V4FreeRegion a, V4FreeRegion b)
            {
                int cmp = a.Bounds.MinY.CompareTo(b.Bounds.MinY);
                if (cmp != 0)
                {
                    return cmp;
                }
                cmp = a.Bounds.MinX.CompareTo(b.Bounds.MinX);
                if (cmp != 0)
                {
                    return cmp;
                }
                return b.Area.CompareTo(a.Area);
            });
            if (regions.Count > HoleCandidateLimit)
            {
                regions.RemoveRange(HoleCandidateLimit, regions.Count - HoleCandidateLimit);
            }
            _freeRegionCache[cacheKey] = regions;
            _timing.FreeRegionsFound += regions.Count;
            return regions;
            }
            finally
            {
                _timing.FreeRegionTicks += V4Timing.Elapsed(timingStart);
                _timing.FreeRegionCalls++;
            }
        }

        private static string FreeRegionCacheKey(ShapeNestingInput input, List<ShapePlacement> board, V4Template template)
        {
            List<string> parts = new List<string>();
            parts.Add(((int)Math.Round(input.BoardWidth * 10.0)).ToString(CultureInfo.InvariantCulture));
            parts.Add(((int)Math.Round(input.BoardHeight * 10.0)).ToString(CultureInfo.InvariantCulture));
            parts.Add(((int)Math.Round(Math.Min(template.Bounds.Width, template.Bounds.Height) * 10.0)).ToString(CultureInfo.InvariantCulture));
            parts.Add(board.Count.ToString(CultureInfo.InvariantCulture));
            foreach (ShapePlacement placement in board)
            {
                parts.Add(((int)Math.Round(placement.Bounds.MinX * 10.0)).ToString(CultureInfo.InvariantCulture) + "," +
                    ((int)Math.Round(placement.Bounds.MinY * 10.0)).ToString(CultureInfo.InvariantCulture) + "," +
                    ((int)Math.Round(placement.Bounds.MaxX * 10.0)).ToString(CultureInfo.InvariantCulture) + "," +
                    ((int)Math.Round(placement.Bounds.MaxY * 10.0)).ToString(CultureInfo.InvariantCulture));
            }
            return string.Join("|", parts.ToArray());
        }

        private static ShapeBounds BoardBounds(ShapeNestingInput input)
        {
            return new ShapeBounds { MinX = 0.0, MinY = 0.0, MaxX = input.BoardWidth, MaxY = input.BoardHeight };
        }

        private static List<List<IntPoint>> BoardOccupiedPaths(List<ShapePlacement> board)
        {
            List<List<IntPoint>> paths = new List<List<IntPoint>>();
            foreach (ShapePlacement placement in board)
            {
                foreach (ShapePolygon polygon in ActualPolygons(placement))
                {
                    List<IntPoint> path = ToClipperPath(polygon);
                    if (path.Count >= 3)
                    {
                        paths.Add(path);
                    }
                }
            }
            if (paths.Count == 0)
            {
                return paths;
            }
            try
            {
                return Clipper.SimplifyPolygons(paths, PolyFillType.pftNonZero);
            }
            catch
            {
                return paths;
            }
        }

        private static V4FreeRegion MakeFreeRegion(ShapeBounds bounds)
        {
            V4FreeRegion region = new V4FreeRegion();
            region.Bounds = bounds;
            region.Area = Math.Max(0.0, bounds.Width * bounds.Height);
            region.CornerPoints = new List<ShapePoint>
            {
                new ShapePoint(bounds.MinX, bounds.MinY),
                new ShapePoint(bounds.MaxX, bounds.MinY),
                new ShapePoint(bounds.MinX, bounds.MaxY),
                new ShapePoint(bounds.MaxX, bounds.MaxY)
            };
            return region;
        }

        private static V4FreeRegion MakeFreeRegion(List<IntPoint> path)
        {
            List<ShapePoint> points = new List<ShapePoint>();
            foreach (IntPoint point in path)
            {
                points.Add(FromClipperPoint(point));
            }
            ShapeBounds bounds = new ShapePolygon(points).Bounds();
            V4FreeRegion region = new V4FreeRegion();
            region.Bounds = bounds;
            region.Area = Math.Abs(Clipper.Area(path)) / (NfpScale * NfpScale);
            region.CornerPoints = SelectFreeRegionCorners(points, bounds);
            return region;
        }

        private static List<ShapePoint> SelectFreeRegionCorners(List<ShapePoint> points, ShapeBounds bounds)
        {
            List<ShapePoint> selected = new List<ShapePoint>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            points.Sort(delegate (ShapePoint a, ShapePoint b)
            {
                double ascore = (a.Y - bounds.MinY) * 100000.0 + (a.X - bounds.MinX);
                double bscore = (b.Y - bounds.MinY) * 100000.0 + (b.X - bounds.MinX);
                return ascore.CompareTo(bscore);
            });
            for (int i = 0; i < points.Count && selected.Count < 14; i++)
            {
                AddSelectedTemplatePoint(selected, seen, points[i]);
            }
            AddSelectedTemplatePoint(selected, seen, new ShapePoint(bounds.MinX, bounds.MinY));
            AddSelectedTemplatePoint(selected, seen, new ShapePoint(bounds.MaxX, bounds.MinY));
            AddSelectedTemplatePoint(selected, seen, new ShapePoint(bounds.MinX, bounds.MaxY));
            return selected;
        }

        private static List<ShapePlacement> NfpAnchorPlacements(List<ShapePlacement> board)
        {
            List<ShapePlacement> anchors = new List<ShapePlacement>(board);
            anchors.Sort(delegate (ShapePlacement a, ShapePlacement b)
            {
                double ay = Math.Min(a.Bounds.MinY, a.Bounds.MaxY);
                double by = Math.Min(b.Bounds.MinY, b.Bounds.MaxY);
                int cmp = by.CompareTo(ay);
                if (cmp != 0)
                {
                    return cmp;
                }
                return b.Bounds.MaxX.CompareTo(a.Bounds.MaxX);
            });
            if (anchors.Count > NfpBoardPlacementLimit)
            {
                anchors.RemoveRange(NfpBoardPlacementLimit, anchors.Count - NfpBoardPlacementLimit);
            }
            return anchors;
        }

        private static int TemplatePointCount(V4Template template)
        {
            int count = 0;
            foreach (ShapePolygon polygon in template.Polygons)
            {
                if (polygon != null && polygon.Points != null)
                {
                    count += polygon.Points.Count;
                }
            }
            return count;
        }

        private static ShapePartInstance CreateNfpProbePart(V4Template template)
        {
            ShapePartInstance group = new ShapePartInstance();
            group.TypeIndex = -1;
            group.Number = 0;
            group.Name = "NFP";
            group.Polygon = BoundsPolygon(template.Bounds);
            group.ColorIndex = 7;
            group.Components = new List<ShapePartComponent>();
            for (int i = 0; i < template.Polygons.Count; i++)
            {
                ShapePartInstance part = new ShapePartInstance
                {
                    TypeIndex = -1,
                    Number = i + 1,
                    Name = "NFP",
                    Polygon = template.Polygons[i],
                    ColorIndex = 7
                };
                group.Components.Add(new ShapePartComponent { Part = part, LocalPolygon = template.Polygons[i] });
            }
            return group;
        }

        private static IEnumerable<ShapePoint> NfpBoundaryCandidates(V4Template fixedTemplate, V4Template movingTemplate, double fixedX, double fixedY)
        {
            List<ShapePoint> result = new List<ShapePoint>();
            if (fixedTemplate == null || movingTemplate == null ||
                fixedTemplate.Polygons == null || movingTemplate.Polygons == null ||
                fixedTemplate.Polygons.Count == 0 || movingTemplate.Polygons.Count == 0)
            {
                return result;
            }

            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            int emitted = 0;
            foreach (ShapePolygon fixedLocal in fixedTemplate.Polygons)
            {
                foreach (ShapePolygon movingLocal in movingTemplate.Polygons)
                {
                    if (emitted >= NfpCandidateLimit)
                    {
                        return result;
                    }
                    foreach (ShapePoint offset in PairNfpBoundaryOffsets(fixedLocal, movingLocal))
                    {
                        if (emitted >= NfpCandidateLimit)
                        {
                            return result;
                        }
                        AddSelectedTemplatePoint(result, seen, new ShapePoint(fixedX + offset.X, fixedY + offset.Y));
                        emitted++;
                    }
                }
            }
            return result;
        }

        private static List<ShapePoint> PairNfpBoundaryOffsets(ShapePolygon fixedLocal, ShapePolygon movingLocal)
        {
            string key = NfpPolygonKey(fixedLocal) + ">" + NfpPolygonKey(movingLocal);
            List<ShapePoint> cached;
            if (_nfpBoundaryCache.TryGetValue(key, out cached))
            {
                return cached;
            }

            List<ShapePoint> result = new List<ShapePoint>();
            List<IntPoint> fixedPath = ToClipperPath(fixedLocal);
            List<IntPoint> movingPath = ToClipperPath(NegatePolygon(movingLocal));
            if (fixedPath.Count < 3 || movingPath.Count < 3)
            {
                _nfpBoundaryCache[key] = result;
                return result;
            }

            try
            {
                List<List<IntPoint>> nfps = Clipper.MinkowskiSum(fixedPath, movingPath, true);
                ShapePoint movingAnchor = movingLocal.Points.Count > 0 ? movingLocal.Points[0] : new ShapePoint(0.0, 0.0);
                Dictionary<string, bool> seen = new Dictionary<string, bool>();
                foreach (List<IntPoint> path in SelectNfpPaths(nfps))
                {
                    AddNfpPathExtremes(result, seen, path, movingAnchor);
                    int step = Math.Max(1, path.Count / 28);
                    for (int i = 0; i < path.Count && result.Count < NfpBoundaryCachePointLimit; i += step)
                    {
                        AddNfpPathPoint(result, seen, path, i, movingAnchor);
                    }
                    if (result.Count >= NfpBoundaryCachePointLimit)
                    {
                        break;
                    }
                }
            }
            catch
            {
            }
            _nfpBoundaryCache[key] = result;
            return result;
        }

        private static void AddNfpPathExtremes(List<ShapePoint> result, Dictionary<string, bool> seen, List<IntPoint> path, ShapePoint movingAnchor)
        {
            if (path == null || path.Count == 0)
            {
                return;
            }
            int minX = 0;
            int maxX = 0;
            int minY = 0;
            int maxY = 0;
            int minSum = 0;
            int maxSum = 0;
            int minDiff = 0;
            int maxDiff = 0;
            for (int i = 1; i < path.Count; i++)
            {
                if (path[i].X < path[minX].X) minX = i;
                if (path[i].X > path[maxX].X) maxX = i;
                if (path[i].Y < path[minY].Y) minY = i;
                if (path[i].Y > path[maxY].Y) maxY = i;
                long sum = path[i].X + path[i].Y;
                long minSumValue = path[minSum].X + path[minSum].Y;
                long maxSumValue = path[maxSum].X + path[maxSum].Y;
                long diff = path[i].X - path[i].Y;
                long minDiffValue = path[minDiff].X - path[minDiff].Y;
                long maxDiffValue = path[maxDiff].X - path[maxDiff].Y;
                if (sum < minSumValue) minSum = i;
                if (sum > maxSumValue) maxSum = i;
                if (diff < minDiffValue) minDiff = i;
                if (diff > maxDiffValue) maxDiff = i;
            }

            AddNfpPathPoint(result, seen, path, minX, movingAnchor);
            AddNfpPathPoint(result, seen, path, maxX, movingAnchor);
            AddNfpPathPoint(result, seen, path, minY, movingAnchor);
            AddNfpPathPoint(result, seen, path, maxY, movingAnchor);
            AddNfpPathPoint(result, seen, path, minSum, movingAnchor);
            AddNfpPathPoint(result, seen, path, maxSum, movingAnchor);
            AddNfpPathPoint(result, seen, path, minDiff, movingAnchor);
            AddNfpPathPoint(result, seen, path, maxDiff, movingAnchor);
        }

        private static void AddNfpPathPoint(List<ShapePoint> result, Dictionary<string, bool> seen, List<IntPoint> path, int index, ShapePoint movingAnchor)
        {
            if (index < 0 || index >= path.Count || result.Count >= NfpBoundaryCachePointLimit)
            {
                return;
            }
            ShapePoint p = FromClipperPoint(path[index]);
            AddSelectedTemplatePoint(result, seen, new ShapePoint(p.X + movingAnchor.X, p.Y + movingAnchor.Y));
        }

        private static IEnumerable<List<IntPoint>> SelectNfpPaths(List<List<IntPoint>> paths)
        {
            if (paths == null)
            {
                yield break;
            }
            paths.Sort(delegate (List<IntPoint> a, List<IntPoint> b)
            {
                double aa = Math.Abs(Clipper.Area(a));
                double ba = Math.Abs(Clipper.Area(b));
                return ba.CompareTo(aa);
            });
            int count = Math.Min(paths.Count, 3);
            for (int i = 0; i < count; i++)
            {
                if (paths[i] != null && paths[i].Count >= 3)
                {
                    yield return paths[i];
                }
            }
        }

        private static List<IntPoint> ToClipperPath(ShapePolygon polygon)
        {
            List<IntPoint> path = new List<IntPoint>();
            foreach (ShapePoint point in polygon.Points)
            {
                path.Add(new IntPoint((long)Math.Round(point.X * NfpScale), (long)Math.Round(point.Y * NfpScale)));
            }
            return Clipper.CleanPolygon(path, 1.0);
        }

        private static ShapePolygon NegatePolygon(ShapePolygon polygon)
        {
            List<ShapePoint> points = new List<ShapePoint>();
            for (int i = polygon.Points.Count - 1; i >= 0; i--)
            {
                ShapePoint p = polygon.Points[i];
                points.Add(new ShapePoint(-p.X, -p.Y));
            }
            return new ShapePolygon(points);
        }

        private static string NfpPolygonKey(ShapePolygon polygon)
        {
            ShapeBounds b = polygon.Bounds();
            List<string> parts = new List<string>();
            int step = Math.Max(1, polygon.Points.Count / 24);
            for (int i = 0; i < polygon.Points.Count; i += step)
            {
                ShapePoint p = polygon.Points[i];
                parts.Add(((int)Math.Round((p.X - b.MinX) * 10.0)).ToString(CultureInfo.InvariantCulture) + "," +
                    ((int)Math.Round((p.Y - b.MinY) * 10.0)).ToString(CultureInfo.InvariantCulture));
            }
            return polygon.Points.Count.ToString(CultureInfo.InvariantCulture) + ":" +
                ((int)Math.Round(b.MinX * 10.0)).ToString(CultureInfo.InvariantCulture) + ":" +
                ((int)Math.Round(b.MinY * 10.0)).ToString(CultureInfo.InvariantCulture) + ":" +
                ((int)Math.Round(b.Width * 10.0)).ToString(CultureInfo.InvariantCulture) + ":" +
                ((int)Math.Round(b.Height * 10.0)).ToString(CultureInfo.InvariantCulture) + ":" +
                string.Join(";", parts.ToArray());
        }

        private static ShapePoint FromClipperPoint(IntPoint point)
        {
            return new ShapePoint(point.X / NfpScale, point.Y / NfpScale);
        }

        private static void AddShelfCandidatePoints(List<ShapePoint> points, Dictionary<string, bool> seen, List<ShapePlacement> board, V4Template template)
        {
            double usedX = UsedX(board);
            AddCandidate(points, seen, usedX + Gap, 0.0);
            foreach (ShapePlacement existing in board)
            {
                AddCandidate(points, seen, existing.Bounds.MaxX + Gap, existing.Bounds.MinY);
                AddCandidate(points, seen, 0.0, existing.Bounds.MaxY + Gap);
                AddCandidate(points, seen, Math.Max(0.0, existing.Bounds.MaxX - template.Bounds.Width), existing.Bounds.MaxY + Gap);
            }
        }

        private static List<ShapePoint> HoleCandidatePoints(ShapeNestingInput input, List<ShapePlacement> board, V4Template template, V4Hole hole)
        {
            List<ShapePoint> points = CandidatePoints(input, board, template, true);
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            List<ShapePoint> preferred = new List<ShapePoint>();
            foreach (ShapePoint point in points)
            {
                if (hole.Internal)
                {
                    if (point.X >= hole.MinX - 1e-6 && point.Y >= hole.MinY - 1e-6 &&
                        point.X + template.Bounds.Width <= hole.MaxX + 1e-6 &&
                        point.Y + template.Bounds.Height <= hole.MaxY + 1e-6)
                    {
                        AddCandidate(preferred, seen, point.X, point.Y);
                    }
                    else if (point.X <= hole.MaxX + Gap && point.Y <= hole.MaxY + Gap &&
                        point.X + template.Bounds.Width >= hole.MinX - Gap &&
                        point.Y + template.Bounds.Height >= hole.MinY - Gap)
                    {
                        AddCandidate(preferred, seen, point.X, point.Y);
                    }
                }
                else if (point.X <= hole.MaxX + Gap && point.Y <= hole.MaxY + Gap &&
                    point.X + template.Bounds.Width >= hole.MinX - Gap && point.Y + template.Bounds.Height >= hole.MinY - Gap)
                {
                    AddCandidate(preferred, seen, point.X, point.Y);
                }
            }
            AddCandidate(preferred, seen, hole.MinX, hole.MinY);
            AddCandidate(preferred, seen, Math.Max(0.0, hole.MaxX - template.Bounds.Width), hole.MinY);
            AddCandidate(preferred, seen, hole.MinX, Math.Max(0.0, hole.MaxY - template.Bounds.Height));
            AddCandidate(preferred, seen, Math.Max(0.0, hole.MaxX - template.Bounds.Width), Math.Max(0.0, hole.MaxY - template.Bounds.Height));
            preferred = LimitPoints(preferred, FillCandidateLimit);
            return CoarseFilterCandidatePoints(input, board, template, preferred, true);
        }

        private static List<ShapePoint> CoarseFilterCandidatePoints(ShapeNestingInput input, List<ShapePlacement> board, V4Template template, List<ShapePoint> points, bool fillMode)
        {
            return CoarseFilterCandidatePoints(input, board, template, points, fillMode, null);
        }

        private static List<ShapePoint> CoarseFilterCandidatePoints(ShapeNestingInput input, List<ShapePlacement> board, V4Template template, List<ShapePoint> points, bool fillMode, List<V4FreeRegion> regions)
        {
            if (points.Count == 0)
            {
                return points;
            }

            int limit = fillMode ? CoarseFillCandidateLimit : CoarseCandidateLimit;
            if (board.Count < 8 || points.Count <= limit)
            {
                return points;
            }

            double usedX = UsedX(board);
            double usedY = UsedY(board);
            double maxUsefulX = Math.Min(input.BoardWidth, Math.Max(usedX + template.Bounds.Width * 1.75 + 220.0, template.Bounds.Width + 220.0));
            List<V4ScoredPoint> scored = new List<V4ScoredPoint>();
            foreach (ShapePoint point in points)
            {
                ShapeBounds bounds = TranslateBounds(template.Bounds, point.X, point.Y);
                if (!FitsBoard(input, bounds))
                {
                    continue;
                }

                double score = CoarseCandidateScore(input, board, template, point, bounds, usedX, usedY, maxUsefulX, fillMode, regions);
                scored.Add(new V4ScoredPoint { Point = point, Score = score });
            }

            scored.Sort(delegate (V4ScoredPoint a, V4ScoredPoint b)
            {
                int cmp = a.Score.CompareTo(b.Score);
                if (cmp != 0)
                {
                    return cmp;
                }
                cmp = a.Point.Y.CompareTo(b.Point.Y);
                return cmp != 0 ? cmp : a.Point.X.CompareTo(b.Point.X);
            });

            List<ShapePoint> selected = new List<ShapePoint>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            for (int i = 0; i < scored.Count && selected.Count < limit; i++)
            {
                AddSelectedTemplatePoint(selected, seen, scored[i].Point);
            }

            AddSelectedTemplatePoint(selected, seen, new ShapePoint(0.0, 0.0));
            if (board.Count > 0)
            {
                AddSelectedTemplatePoint(selected, seen, new ShapePoint(Math.Max(0.0, usedX + Gap), 0.0));
                AddSelectedTemplatePoint(selected, seen, new ShapePoint(0.0, Math.Max(0.0, usedY + Gap)));
            }

            selected.Sort(delegate (ShapePoint a, ShapePoint b)
            {
                int cmp = a.Y.CompareTo(b.Y);
                return cmp != 0 ? cmp : a.X.CompareTo(b.X);
            });
            return selected;
        }

        private static double CoarseCandidateScore(ShapeNestingInput input, List<ShapePlacement> board, V4Template template, ShapePoint point, ShapeBounds bounds, double usedX, double usedY, double maxUsefulX, bool fillMode, List<V4FreeRegion> regions)
        {
            double addedX = Math.Max(0.0, bounds.MaxX - usedX);
            double farPenalty = Math.Max(0.0, bounds.MaxX - maxUsefulX) * input.BoardHeight * 80.0;
            double score = bounds.MinY * 1200000.0 +
                bounds.MinX * 260000.0 +
                bounds.MaxY * 2500.0 +
                addedX * (fillMode ? 900000.0 : 520000.0) +
                farPenalty;
            score -= FreeRegionAffinity(bounds, regions) * input.BoardWidth * input.BoardHeight * 2.4;

            double bestGap = double.PositiveInfinity;
            double contact = 0.0;
            foreach (ShapePlacement existing in board)
            {
                double xGap = AxisGap(bounds.MinX, bounds.MaxX, existing.Bounds.MinX, existing.Bounds.MaxX);
                double yGap = AxisGap(bounds.MinY, bounds.MaxY, existing.Bounds.MinY, existing.Bounds.MaxY);
                double coarseGap = Math.Max(xGap, yGap);
                if (coarseGap < bestGap)
                {
                    bestGap = coarseGap;
                }
                contact += BoundsContact(bounds, existing.Bounds, 10.0);
            }
            if (!double.IsInfinity(bestGap))
            {
                score += Math.Min(bestGap, 160.0) * 85000.0;
            }
            score -= contact * 260000.0;
            if (bounds.MaxX <= usedX + 1e-6 && usedX > 1e-6)
            {
                score -= input.BoardWidth * 140000.0;
            }
            if (bounds.MaxY <= usedY + template.Bounds.Height * 0.5 + 1e-6)
            {
                score -= input.BoardHeight * 90000.0;
            }
            return score;
        }

        private static double AxisGap(double aMin, double aMax, double bMin, double bMax)
        {
            if (aMax < bMin)
            {
                return bMin - aMax;
            }
            if (bMax < aMin)
            {
                return aMin - bMax;
            }
            return 0.0;
        }

        private static V4Hole FindBestHole(ShapeNestingInput input, List<ShapePlacement> board)
        {
            List<V4Hole> holes = FindHoles(input, board);
            return holes.Count > 0 ? holes[0] : null;
        }

        private static List<V4Hole> FindHoles(ShapeNestingInput input, List<ShapePlacement> board)
        {
            long timingStart = V4Timing.Start();
            try
            {
            if (board.Count == 0)
            {
                return new List<V4Hole>();
            }
            double usedX = UsedX(board);
            List<double> xs = new List<double> { 0.0, usedX, input.BoardWidth };
            List<double> ys = new List<double> { 0.0, input.BoardHeight };
            foreach (ShapePlacement p in board)
            {
                xs.Add(Math.Max(0.0, p.Bounds.MinX));
                xs.Add(Math.Min(input.BoardWidth, p.Bounds.MaxX));
                ys.Add(Math.Max(0.0, p.Bounds.MinY));
                ys.Add(Math.Min(input.BoardHeight, p.Bounds.MaxY));
            }
            xs.Sort();
            ys.Sort();

            List<V4Hole> holes = new List<V4Hole>();
            for (int xi = 0; xi < xs.Count - 1; xi++)
            {
                for (int yi = 0; yi < ys.Count - 1; yi++)
                {
                    double minX = xs[xi];
                    double maxX = xs[xi + 1];
                    double minY = ys[yi];
                    double maxY = ys[yi + 1];
                    if (maxX - minX < 25.0 || maxY - minY < 25.0)
                    {
                        continue;
                    }
                    ShapePoint center = new ShapePoint((minX + maxX) / 2.0, (minY + maxY) / 2.0);
                    if (PointInsideAny(center, board))
                    {
                        continue;
                    }
                    double area = (maxX - minX) * (maxY - minY);
                    bool internalHole = maxX <= usedX + 1e-6;
                    double score = internalHole ?
                        -area * 18.0 + minX * 180.0 + minY * 8.0 :
                        -area * 35.0 + minX * 260.0 + minY * 35.0;
                    holes.Add(new V4Hole
                    {
                        MinX = minX,
                        MinY = minY,
                        MaxX = maxX,
                        MaxY = maxY,
                        Internal = internalHole,
                        SortScore = score
                    });
                }
            }
            AddTopBandHoles(input, board, usedX, holes);
            holes.Sort(delegate (V4Hole a, V4Hole b)
            {
                if (a.Internal != b.Internal)
                {
                    return a.Internal ? -1 : 1;
                }
                return a.SortScore.CompareTo(b.SortScore);
            });
            if (holes.Count > HoleCandidateLimit)
            {
                holes.RemoveRange(HoleCandidateLimit, holes.Count - HoleCandidateLimit);
            }
            _timing.HolesFound += holes.Count;
            return holes;
            }
            finally
            {
                _timing.HoleTicks += V4Timing.Elapsed(timingStart);
                _timing.HoleCalls++;
            }
        }

        private static bool PointInsideAny(ShapePoint point, List<ShapePlacement> board)
        {
            foreach (ShapePlacement placement in board)
            {
                if (point.X < placement.Bounds.MinX || point.X > placement.Bounds.MaxX ||
                    point.Y < placement.Bounds.MinY || point.Y > placement.Bounds.MaxY)
                {
                    continue;
                }
                foreach (ShapePolygon polygon in ActualPolygons(placement))
                {
                    ShapeBounds bounds = polygon.Bounds();
                    if (point.X >= bounds.MinX && point.X <= bounds.MaxX &&
                        point.Y >= bounds.MinY && point.Y <= bounds.MaxY &&
                        PointInPolygon(point, polygon))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool PointInPolygon(ShapePoint point, ShapePolygon polygon)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Points.Count - 1; i < polygon.Points.Count; j = i++)
            {
                ShapePoint pi = polygon.Points[i];
                ShapePoint pj = polygon.Points[j];
                bool crosses = ((pi.Y > point.Y) != (pj.Y > point.Y)) &&
                    (point.X < (pj.X - pi.X) * (point.Y - pi.Y) / Math.Max(1e-12, pj.Y - pi.Y) + pi.X);
                if (crosses)
                {
                    inside = !inside;
                }
            }
            return inside;
        }

        private static double MainScore(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement placement, V4Template template, V4Strategy strategy)
        {
            double usedX = UsedX(board);
            double addedX = Math.Max(0.0, placement.Bounds.MaxX - usedX);
            double xMisalign = GridMisalignment(board, placement, true);
            double yMisalign = GridMisalignment(board, placement, false);
            double near = NearReward(board, placement, 60.0);
            double contact = ContactReward(board, placement);
            double insidePenalty = board.Count > 0 && placement.Bounds.MaxX <= usedX + 1e-6 ? 4500000.0 : 0.0;
            double shelfPenalty = ShelfPlacementPenalty(board, placement, template);
            double orientationPenalty = PairOrientationPenalty(template, strategy) * input.BoardWidth * 650.0;
            return addedX * 16000000.0 +
                placement.Bounds.MaxX * 2600.0 +
                placement.Bounds.MinX * 700.0 +
                placement.Bounds.MaxY * 180.0 +
                placement.Bounds.MinY * 900.0 +
                xMisalign * 42000.0 +
                yMisalign * 26000.0 +
                insidePenalty +
                shelfPenalty +
                orientationPenalty +
                template.Bounds.Width * template.Bounds.Height * strategy.TemplateAreaWeight -
                near * 1500000.0 -
                contact * 850000.0;
        }

        private static double ShelfPlacementPenalty(List<ShapePlacement> board, ShapePlacement placement, V4Template template)
        {
            if (!template.ShelfLayout)
            {
                return 0.0;
            }
            double usedX = UsedX(board);
            double expectedX = board.Count == 0 ? 0.0 : usedX + Gap;
            double continueRow = Math.Abs(placement.Bounds.MinX - expectedX) * 250000.0 + placement.Bounds.MinY * 450000.0;
            double newRow = placement.Bounds.MinX * 450000.0 + Math.Max(0.0, placement.Bounds.MinY) * 40000.0;
            return Math.Min(continueRow, newRow);
        }

        private static double GridMisalignment(List<ShapePlacement> board, ShapePlacement placement, bool xAxis)
        {
            if (board.Count == 0)
            {
                return 0.0;
            }

            double value = xAxis ? placement.Bounds.MinX : placement.Bounds.MinY;
            double best = Math.Abs(value);
            foreach (ShapePlacement existing in board)
            {
                double a = xAxis ? existing.Bounds.MinX : existing.Bounds.MinY;
                double b = xAxis ? existing.Bounds.MaxX + Gap : existing.Bounds.MaxY + Gap;
                best = Math.Min(best, Math.Abs(value - a));
                best = Math.Min(best, Math.Abs(value - b));
            }
            return Math.Min(best, 80.0);
        }

        private static double FillScore(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement placement, V4Template template, V4Hole hole, int rank)
        {
            double usedX = UsedX(board);
            double addedX = Math.Max(0.0, placement.Bounds.MaxX - usedX);
            double boardArea = input.BoardWidth * input.BoardHeight;
            double holePenalty = 0.0;
            double holeUseReward = 0.0;
            double countReward = 0.0;
            double addedXWeight = 120000000.0;
            if (hole != null)
            {
                double cx = (placement.Bounds.MinX + placement.Bounds.MaxX) / 2.0;
                double cy = (placement.Bounds.MinY + placement.Bounds.MaxY) / 2.0;
                double hx = (hole.MinX + hole.MaxX) / 2.0;
                double hy = (hole.MinY + hole.MaxY) / 2.0;
                holePenalty = Math.Abs(cx - hx) * 1200.0 + Math.Abs(cy - hy) * 80.0;
                double holeArea = Math.Max(1.0, (hole.MaxX - hole.MinX) * (hole.MaxY - hole.MinY));
                double fillRatio = Math.Min(1.0, template.Bounds.Width * template.Bounds.Height / holeArea);
                if (!hole.Internal)
                {
                    addedXWeight = 6000000.0;
                    holeUseReward = fillRatio * boardArea * 720.0;
                    countReward = template.Count * boardArea * 160.0;
                }
                else
                {
                    holeUseReward = fillRatio * boardArea * 180.0;
                    countReward = template.Count * boardArea * 45.0;
                }
            }
            return addedX * addedXWeight +
                placement.Bounds.MinX * 1600.0 +
                placement.Bounds.MinY * 80.0 +
                rank * 500000.0 +
                holePenalty -
                holeUseReward -
                countReward -
                NearReward(board, placement, 55.0) * 850000.0 -
                ContactReward(board, placement) * 520000.0;
        }

        private static double ContactReward(List<ShapePlacement> board, ShapePlacement placement)
        {
            double reward = 0.0;
            List<ShapePolygon> placedPolygons = ActualPolygons(placement);
            List<ShapeBounds> placedBounds = ActualPolygonBounds(placement);
            foreach (ShapePlacement existing in NearbyPlacements(board, placement.Bounds, 12.0))
            {
                reward += BoundsContact(placement.Bounds, existing.Bounds, 8.0);
                List<ShapePolygon> existingPolygons = ActualPolygons(existing);
                List<ShapeBounds> existingBounds = ActualPolygonBounds(existing);
                for (int ai = 0; ai < placedPolygons.Count; ai++)
                {
                    ShapePolygon a = placedPolygons[ai];
                    ShapeBounds ab = placedBounds[ai];
                    for (int bi = 0; bi < existingPolygons.Count; bi++)
                    {
                        ShapePolygon b = existingPolygons[bi];
                        ShapeBounds bb = existingBounds[bi];
                        reward += EdgeNearReward(a, b, 6.0) * 0.25;
                        reward += BoundsContact(ab, bb, 4.0) * 2.0;
                    }
                }
            }
            return reward;
        }

        private static double NearReward(List<ShapePlacement> board, ShapePlacement placement, double maxDistance)
        {
            double reward = 0.0;
            List<ShapePolygon> placedPolygons = ActualPolygons(placement);
            foreach (ShapePlacement existing in NearbyPlacements(board, placement.Bounds, maxDistance + 8.0))
            {
                List<ShapePolygon> existingPolygons = ActualPolygons(existing);
                foreach (ShapePolygon a in placedPolygons)
                {
                    foreach (ShapePolygon b in existingPolygons)
                    {
                        reward += ShapeNearReward(a, b, maxDistance);
                    }
                }
            }
            return reward;
        }

        private static bool HitsAny(ShapePlacement placement, List<ShapePlacement> board)
        {
            long timingStart = V4Timing.Start();
            try
            {
            List<ShapePolygon> candidatePolygons = ActualPolygons(placement);
            List<ShapeBounds> candidateBounds = ActualPolygonBounds(placement);
            foreach (ShapePlacement existing in NearbyPlacements(board, placement.Bounds, 1.0))
            {
                if (!ShapeCollision.BoundsOverlap(placement.Bounds, existing.Bounds))
                {
                    continue;
                }
                List<ShapePolygon> existingPolygons = ActualPolygons(existing);
                List<ShapeBounds> existingBounds = ActualPolygonBounds(existing);
                for (int ai = 0; ai < candidatePolygons.Count; ai++)
                {
                    ShapePolygon a = candidatePolygons[ai];
                    ShapeBounds ab = candidateBounds[ai];
                    for (int bi = 0; bi < existingPolygons.Count; bi++)
                    {
                        ShapePolygon b = existingPolygons[bi];
                        ShapeBounds bb = existingBounds[bi];
                        if (ShapeCollision.BoundsOverlap(ab, bb) && ShapeCollision.Intersects(a, ab, b, bb))
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
            }
            finally
            {
                _timing.CollisionTicks += V4Timing.Elapsed(timingStart);
                _timing.CollisionCalls++;
            }
        }

        private static IEnumerable<ShapePlacement> NearbyPlacements(List<ShapePlacement> board, ShapeBounds bounds, double padding)
        {
            if (board.Count <= 24)
            {
                return board;
            }

            List<ShapePlacement> nearby = new List<ShapePlacement>();
            double minX = bounds.MinX - padding;
            double minY = bounds.MinY - padding;
            double maxX = bounds.MaxX + padding;
            double maxY = bounds.MaxY + padding;
            foreach (ShapePlacement existing in board)
            {
                if (existing.Bounds.MaxX < minX || existing.Bounds.MinX > maxX ||
                    existing.Bounds.MaxY < minY || existing.Bounds.MinY > maxY)
                {
                    continue;
                }
                nearby.Add(existing);
            }
            return nearby;
        }

        private static ShapePlacement SettlePlacementBottomLeft(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement placement, V4Template template)
        {
            if (board.Count == 0)
            {
                return MakePlacement(placement.Part, template, placement.BoardIndex, 0.0, 0.0);
            }
            if (SelectiveSlidePolishExperiment)
            {
                return placement;
            }

            return FullSettlePlacementBottomLeft(input, board, placement, template);
        }

        private static ShapePlacement FullSettlePlacementBottomLeft(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement placement, V4Template template)
        {
            long timingStart = V4Timing.Start();
            try
            {
                if (board.Count == 0)
                {
                    return MakePlacement(placement.Part, template, placement.BoardIndex, 0.0, 0.0);
                }
                ShapePlacement nfp = NfpSettlePlacement(input, board, placement, template);
                if (nfp != null)
                {
                    return nfp;
                }

                return placement;
            }
            finally
            {
                _timing.SettleTicks += V4Timing.Elapsed(timingStart);
                _timing.SettleCalls++;
            }
        }

        private static ShapePlacement PolishSelectedPlacement(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement placement)
        {
            if (placement == null || !SelectiveSlidePolishExperiment)
            {
                return placement;
            }

            V4Template template = TemplateFromPlacement(placement);
            ShapePlacement polished = FullSettlePlacementBottomLeft(input, board, placement, template);
            if (IsUsefulPolishedPlacement(input, board, placement, polished, false))
            {
                return polished;
            }
            return placement;
        }

        private static ShapePlacement PolishSelectedHolePlacement(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement placement, V4Hole hole)
        {
            if (placement == null || !SelectiveSlidePolishExperiment)
            {
                return placement;
            }

            V4Template template = TemplateFromPlacement(placement);
            ShapePlacement strict = FullSettlePlacementInsideHole(input, board, placement, template, hole);
            if (IsUsefulPolishedPlacement(input, board, placement, strict, true) && PlacementTouchesHole(strict, hole))
            {
                return strict;
            }

            ShapePlacement polished = FullSettlePlacementBottomLeft(input, board, placement, template);
            if (IsUsefulPolishedPlacement(input, board, placement, polished, true) && PlacementTouchesHole(polished, hole))
            {
                return polished;
            }
            return placement;
        }

        private static bool IsUsefulPolishedPlacement(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement original, ShapePlacement polished, bool allowSmallRightGrowth)
        {
            if (polished == null || !FitsBoard(input, polished.Bounds) || HitsAny(polished, board))
            {
                return false;
            }

            double rightGrowthLimit = allowSmallRightGrowth ? Math.Max(0.5, Gap) : 0.5;
            if (polished.Bounds.MaxX > original.Bounds.MaxX + rightGrowthLimit)
            {
                return false;
            }
            return true;
        }

        private static ShapePlacement NfpSettlePlacement(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement placement, V4Template template)
        {
            List<ShapePoint> points = new List<ShapePoint>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            AddNfpSettleCandidate(points, seen, placement.X, placement.Y);
            AddNfpSettleCandidate(points, seen, 0.0, placement.Y);
            AddNfpSettleCandidate(points, seen, placement.X, 0.0);
            AddNfpSettleCandidate(points, seen, 0.0, 0.0);

            foreach (ShapePlacement existing in NfpAnchorPlacements(board))
            {
                PumpUi(false);
                V4Template fixedTemplate = TemplateFromPlacement(existing);
                foreach (ShapePoint candidate in NfpBoundaryCandidates(fixedTemplate, template, existing.X, existing.Y))
                {
                    AddNfpSettleCandidate(points, seen, candidate.X, candidate.Y);
                    AddNfpSettleCandidate(points, seen, 0.0, candidate.Y);
                    AddNfpSettleCandidate(points, seen, candidate.X, 0.0);
                    if (points.Count >= NfpSettleCandidateLimit)
                    {
                        break;
                    }
                }
                if (points.Count >= NfpSettleCandidateLimit)
                {
                    break;
                }
            }

            points.Sort(delegate (ShapePoint a, ShapePoint b)
            {
                ShapePlacement ap = MakePlacement(placement.Part, template, placement.BoardIndex, a.X, a.Y);
                ShapePlacement bp = MakePlacement(placement.Part, template, placement.BoardIndex, b.X, b.Y);
                double usedX = UsedX(board);
                double ascore = Math.Max(0.0, ap.Bounds.MaxX - usedX) * 1000000.0 + ap.Bounds.MinY * 10000.0 + ap.Bounds.MinX;
                double bscore = Math.Max(0.0, bp.Bounds.MaxX - usedX) * 1000000.0 + bp.Bounds.MinY * 10000.0 + bp.Bounds.MinX;
                return ascore.CompareTo(bscore);
            });

            ShapePlacement best = null;
            double bestScore = double.PositiveInfinity;
            int checkedCount = 0;
            foreach (ShapePoint point in points)
            {
                PumpUi(false);
                if (checkedCount++ >= NfpSettleCandidateLimit)
                {
                    break;
                }
                ShapePlacement candidate = MakePlacement(placement.Part, template, placement.BoardIndex, point.X, point.Y);
                if (!FitsBoard(input, candidate.Bounds) || HitsAny(candidate, board))
                {
                    continue;
                }
                double score = NfpSettleScore(board, candidate);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }
            return best == null ? null : PolishNfpPlacement(input, board, placement, best, template);
        }

        private static double NfpSettleScore(List<ShapePlacement> board, ShapePlacement placement)
        {
            double usedX = UsedX(board);
            double addedX = Math.Max(0.0, placement.Bounds.MaxX - usedX);
            double contact = FastBoundsContactReward(board, placement.Bounds);
            double near = FastBoundsNearReward(board, placement.Bounds, 45.0);
            return addedX * 900000000.0 +
                placement.Bounds.MinY * 180000000.0 +
                placement.Bounds.MinX * 34000000.0 +
                placement.Bounds.MaxY * 180000.0 +
                placement.Bounds.MaxX * 6000.0 -
                contact * 1600000.0 -
                near * 800000.0;
        }

        private static ShapePlacement PolishNfpPlacement(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement original, ShapePlacement placement, V4Template template)
        {
            ShapePlacement best = placement;
            double bestScore = NfpPolishScore(board, best);
            double step = Math.Max(1.0, Math.Min(18.0, Math.Min(template.Bounds.Width, template.Bounds.Height) / 18.0));
            double[,] offsets = new double[,]
            {
                { 0.0, 0.0 },
                { -step, 0.0 },
                { 0.0, -step },
                { -step, -step },
                { step, 0.0 },
                { 0.0, step },
                { step, -step },
                { -step, step },
                { -step * 2.0, 0.0 },
                { 0.0, -step * 2.0 }
            };

            List<V4PlacementChoice> slideSeeds = new List<V4PlacementChoice>();
            for (int i = 0; i < offsets.GetLength(0); i++)
            {
                ShapePlacement seed = MakePlacement(placement.Part, template, placement.BoardIndex, placement.X + offsets[i, 0], placement.Y + offsets[i, 1]);
                TryKeepPolishedNfp(input, board, original, placement, seed, template, ref best, ref bestScore);
                if (FitsBoard(input, seed.Bounds) && !HitsAny(seed, board))
                {
                    slideSeeds.Add(new V4PlacementChoice { Placement = seed, Score = NfpPolishSeedScore(board, seed) });
                }
            }

            slideSeeds.Sort(delegate (V4PlacementChoice a, V4PlacementChoice b)
            {
                return a.Score.CompareTo(b.Score);
            });
            for (int i = 0; i < slideSeeds.Count && i < PolishSlideSeedLimit; i++)
            {
                ShapePlacement seed = slideSeeds[i].Placement;
                ShapePlacement leftFirst = SlidePlacement(input, board, seed, template, -1.0, 0.0);
                leftFirst = SlidePlacement(input, board, leftFirst, template, 0.0, -1.0);
                TryKeepPolishedNfp(input, board, original, placement, leftFirst, template, ref best, ref bestScore);

                ShapePlacement downFirst = SlidePlacement(input, board, seed, template, 0.0, -1.0);
                downFirst = SlidePlacement(input, board, downFirst, template, -1.0, 0.0);
                TryKeepPolishedNfp(input, board, original, placement, downFirst, template, ref best, ref bestScore);
            }
            return best;
        }

        private static double NfpPolishSeedScore(List<ShapePlacement> board, ShapePlacement placement)
        {
            double usedX = UsedX(board);
            double addedX = Math.Max(0.0, placement.Bounds.MaxX - usedX);
            return addedX * 900000000.0 +
                placement.Bounds.MinY * 120000000.0 +
                placement.Bounds.MinX * 26000000.0 +
                placement.Bounds.MaxY * 120000.0 +
                placement.Bounds.MaxX * 4000.0 -
                FastBoundsContactReward(board, placement.Bounds) * 1200000.0;
        }

        private static void TryKeepPolishedNfp(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement original, ShapePlacement selected, ShapePlacement candidate, V4Template template, ref ShapePlacement best, ref double bestScore)
        {
            if (candidate == null || !FitsBoard(input, candidate.Bounds) || HitsAny(candidate, board))
            {
                return;
            }
            double boardUsed = UsedX(board);
            double originalUsed = Math.Max(boardUsed, original.Bounds.MaxX);
            double candidateUsed = Math.Max(boardUsed, candidate.Bounds.MaxX);
            if (candidateUsed > originalUsed + 0.25)
            {
                return;
            }
            double allowedMaxY = Math.Max(original.Bounds.MaxY, selected.Bounds.MaxY) + Math.Max(4.0, template.Bounds.Height * 0.04);
            if (candidate.Bounds.MaxY > allowedMaxY && candidateUsed >= originalUsed - 0.25)
            {
                return;
            }

            double score = NfpPolishScore(board, candidate);
            if (score < bestScore - 0.01)
            {
                bestScore = score;
                best = candidate;
            }
        }

        private static void AddTopBandHoles(ShapeNestingInput input, List<ShapePlacement> board, double usedX, List<V4Hole> holes)
        {
            if (board.Count == 0 || usedX <= 1.0)
            {
                return;
            }

            List<double> xs = new List<double> { 0.0, usedX };
            foreach (ShapePlacement p in board)
            {
                xs.Add(Math.Max(0.0, Math.Min(usedX, p.Bounds.MinX)));
                xs.Add(Math.Max(0.0, Math.Min(usedX, p.Bounds.MaxX)));
            }
            xs.Sort();

            for (int i = 0; i < xs.Count - 1; i++)
            {
                double minX = xs[i];
                double maxX = xs[i + 1];
                if (maxX - minX < 35.0)
                {
                    continue;
                }

                double top = 0.0;
                foreach (ShapePlacement p in board)
                {
                    if (p.Bounds.MinX <= maxX + Gap && p.Bounds.MaxX >= minX - Gap)
                    {
                        top = Math.Max(top, p.Bounds.MaxY);
                    }
                }
                if (top >= input.BoardHeight - 35.0)
                {
                    continue;
                }

                ShapePoint center = new ShapePoint((minX + maxX) / 2.0, (top + input.BoardHeight) / 2.0);
                if (PointInsideAny(center, board))
                {
                    continue;
                }

                double area = (maxX - minX) * (input.BoardHeight - top);
                holes.Add(new V4Hole
                {
                    MinX = minX,
                    MinY = top,
                    MaxX = maxX,
                    MaxY = input.BoardHeight,
                    Internal = true,
                    SortScore = -area * 28.0 + minX * 60.0 - top * 120.0
                });
            }
        }

        private static double NfpPolishScore(List<ShapePlacement> board, ShapePlacement placement)
        {
            double usedX = UsedX(board);
            double addedX = Math.Max(0.0, placement.Bounds.MaxX - usedX);
            double contact = FastBoundsContactReward(board, placement.Bounds);
            double near = FastBoundsNearReward(board, placement.Bounds, 36.0);
            return addedX * 900000000.0 +
                placement.Bounds.MinX * 42000000.0 +
                placement.Bounds.MinY * 140000000.0 +
                placement.Bounds.MaxX * 7000.0 +
                placement.Bounds.MaxY * 160000.0 -
                contact * 2200000.0 -
                near * 900000.0;
        }

        private static double FastBoundsContactReward(List<ShapePlacement> board, ShapeBounds bounds)
        {
            double reward = 0.0;
            foreach (ShapePlacement existing in NearbyPlacements(board, bounds, 12.0))
            {
                reward += BoundsContact(bounds, existing.Bounds, 8.0);
            }
            return reward;
        }

        private static double FastBoundsNearReward(List<ShapePlacement> board, ShapeBounds bounds, double maxDistance)
        {
            double reward = 0.0;
            foreach (ShapePlacement existing in NearbyPlacements(board, bounds, maxDistance + 4.0))
            {
                double xGap = AxisGap(bounds.MinX, bounds.MaxX, existing.Bounds.MinX, existing.Bounds.MaxX);
                double yGap = AxisGap(bounds.MinY, bounds.MaxY, existing.Bounds.MinY, existing.Bounds.MaxY);
                double gap = Math.Sqrt(xGap * xGap + yGap * yGap);
                if (gap <= maxDistance)
                {
                    reward += maxDistance - gap;
                }
            }
            return reward;
        }

        private static void AddNfpSettleCandidate(List<ShapePoint> points, Dictionary<string, bool> seen, double x, double y)
        {
            if (x < -1e-6 || y < -1e-6)
            {
                return;
            }
            AddSelectedTemplatePoint(points, seen, new ShapePoint(Math.Max(0.0, x), Math.Max(0.0, y)));
        }

        private static ShapePlacement LocalTightenPlacement(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement placement, V4Template template)
        {
            ShapePlacement current = placement;
            double baseStep = Math.Max(1.0, Math.Min(template.Bounds.Width, template.Bounds.Height) / 10.0);
            double[] steps = new double[] { Math.Min(60.0, baseStep * 2.0), Math.Min(24.0, baseStep), 6.0, 1.0, 0.25 };
            foreach (double step in steps)
            {
                bool moved = true;
                int guard = 0;
                while (moved && guard < 8)
                {
                    PumpUi(false);
                    moved = false;
                    ShapePlacement best = current;
                    double bestScore = LocalTightenScore(board, current);
                    double[,] offsets = new double[,]
                    {
                        { -step, 0.0 },
                        { 0.0, -step },
                        { -step, -step },
                        { -step, step },
                        { step, -step },
                        { -step * 2.0, step },
                        { step, -step * 2.0 }
                    };
                    for (int i = 0; i < offsets.GetLength(0); i++)
                    {
                        ShapePlacement candidate = MakePlacement(current.Part, template, current.BoardIndex, current.X + offsets[i, 0], current.Y + offsets[i, 1]);
                        if (!FitsBoard(input, candidate.Bounds) || HitsAny(candidate, board))
                        {
                            continue;
                        }
                        candidate = SlidePlacement(input, board, candidate, template, 0.0, -1.0);
                        candidate = SlidePlacement(input, board, candidate, template, -1.0, 0.0);
                        if (!FitsBoard(input, candidate.Bounds) || HitsAny(candidate, board))
                        {
                            continue;
                        }
                        double score = LocalTightenScore(board, candidate);
                        if (score < bestScore - 0.01)
                        {
                            bestScore = score;
                            best = candidate;
                            moved = true;
                        }
                    }
                    current = best;
                    guard++;
                }
            }
            return current;
        }

        private static double LocalTightenScore(List<ShapePlacement> board, ShapePlacement placement)
        {
            double contact = ContactReward(board, placement);
            double near = NearReward(board, placement, 80.0);
            return placement.Bounds.MinX * 12000000.0 +
                placement.Bounds.MinY * 9000000.0 +
                placement.Bounds.MaxX * 50000.0 +
                placement.Bounds.MaxY * 15000.0 -
                near * 1200000.0 -
                contact * 900000.0;
        }

        private static bool IsPlacementTight(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement placement, V4Template template)
        {
            double probe = Math.Max(0.5, Math.Min(8.0, Math.Min(template.Bounds.Width, template.Bounds.Height) / 18.0));
            if (CanPlaceAt(input, board, placement, template, placement.X, placement.Y - probe) ||
                CanPlaceAt(input, board, placement, template, placement.X - probe, placement.Y))
            {
                return false;
            }

            double[] sideSteps = new double[] { probe, probe * 2.0, probe * 4.0 };
            foreach (double side in sideSteps)
            {
                if (CanPlaceAt(input, board, placement, template, placement.X - side, placement.Y) &&
                    CanPlaceAt(input, board, placement, template, placement.X - side, placement.Y - probe))
                {
                    return false;
                }
                if (CanPlaceAt(input, board, placement, template, placement.X + side, placement.Y) &&
                    CanPlaceAt(input, board, placement, template, placement.X + side, placement.Y - probe))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool CanPlaceAt(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement placement, V4Template template, double x, double y)
        {
            ShapePlacement candidate = MakePlacement(placement.Part, template, placement.BoardIndex, x, y);
            return FitsBoard(input, candidate.Bounds) && !HitsAny(candidate, board);
        }

        private static ShapePlacement StepSettlePlacement(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement placement, V4Template template)
        {
            ShapePlacement current = placement;
            double baseStep = Math.Max(2.0, Math.Min(template.Bounds.Width, template.Bounds.Height) / 2.0);
            double[] steps = new double[] { baseStep, Math.Max(2.0, baseStep / 4.0), 2.0, 0.5 };
            foreach (double step in steps)
            {
                PumpUi(false);
                bool moved;
                int guard = 0;
                do
                {
                    PumpUi(false);
                    moved = false;
                    ShapePlacement best = BestStepDropFromScan(input, board, current, template, step);
                    if (Math.Abs(best.X - current.X) > 0.01 || Math.Abs(best.Y - current.Y) > 0.01)
                    {
                        current = best;
                        moved = true;
                    }
                    guard++;
                } while (moved && guard < 8);
            }
            return current;
        }

        private static ShapePlacement BestStepDropFromScan(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement current, V4Template template, double step)
        {
            ShapePlacement best = current;
            double bestScore = BottomLeftSettleScore(board, current);
            int span = 2;
            for (int offset = 0; offset <= span; offset++)
            {
                PumpUi(false);
                int[] signs = offset == 0 ? new int[] { 0 } : new int[] { -1, 1 };
                foreach (int sign in signs)
                {
                    double x = current.X + sign * offset * step;
                    ShapePlacement candidate = MakePlacement(current.Part, template, current.BoardIndex, x, current.Y);
                    if (!FitsBoard(input, candidate.Bounds) || HitsAny(candidate, board))
                    {
                        continue;
                    }
                    candidate = SlidePlacement(input, board, candidate, template, 0.0, -1.0);
                    candidate = SlidePlacement(input, board, candidate, template, -1.0, 0.0);
                    candidate = SlidePlacement(input, board, candidate, template, 0.0, -1.0);
                    if (!FitsBoard(input, candidate.Bounds) || HitsAny(candidate, board))
                    {
                        continue;
                    }
                    double score = BottomLeftSettleScore(board, candidate);
                    if (score < bestScore - 0.01)
                    {
                        bestScore = score;
                        best = candidate;
                    }
                }
            }
            return best;
        }

        private static ShapePlacement DropThenLeft(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement placement, V4Template template)
        {
            ShapePlacement current = placement;
            for (int i = 0; i < 2; i++)
            {
                PumpUi(false);
                current = SlidePlacement(input, board, current, template, 0.0, -1.0);
                current = SlidePlacement(input, board, current, template, -1.0, 0.0);
            }
            return current;
        }

        private static ShapePlacement BestBottomLeftEdgePlacement(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement placement, V4Template template)
        {
            double rightLimit = Math.Max(placement.Bounds.MaxX, UsedX(board));
            List<double> xs = SettleAxisCandidates(input, board, template, placement.X, Math.Max(placement.X, rightLimit - template.Bounds.MaxX), true);
            List<double> ys = SettleAxisCandidates(input, board, template, placement.Y, placement.Y, false);
            ShapePlacement best = placement;
            double bestScore = BottomLeftSettleScore(board, placement);

            foreach (double x in xs)
            {
                PumpUi(false);
                ShapePlacement candidate = MakePlacement(placement.Part, template, placement.BoardIndex, x, placement.Y);
                if (candidate.Bounds.MaxX > rightLimit + 1e-6)
                {
                    continue;
                }
                if (!FitsBoard(input, candidate.Bounds) || HitsAny(candidate, board))
                {
                    continue;
                }
                candidate = DropThenLeft(input, board, candidate, template);
                if (candidate.Bounds.MaxX > rightLimit + 1e-6 || !FitsBoard(input, candidate.Bounds) || HitsAny(candidate, board))
                {
                    continue;
                }
                double score = BottomLeftSettleScore(board, candidate);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            foreach (double y in ys)
            {
                PumpUi(false);
                ShapePlacement candidate = MakePlacement(placement.Part, template, placement.BoardIndex, best.X, y);
                if (candidate.Bounds.MaxX > rightLimit + 1e-6)
                {
                    continue;
                }
                if (!FitsBoard(input, candidate.Bounds) || HitsAny(candidate, board))
                {
                    continue;
                }
                candidate = DropThenLeft(input, board, candidate, template);
                if (candidate.Bounds.MaxX > rightLimit + 1e-6 || !FitsBoard(input, candidate.Bounds) || HitsAny(candidate, board))
                {
                    continue;
                }
                double score = BottomLeftSettleScore(board, candidate);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            return best;
        }

        private static ShapePlacement SlidePlacement(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement placement, V4Template template, double dx, double dy)
        {
            if ((dx < 0.0 && placement.X <= 1e-6) || (dy < 0.0 && placement.Y <= 1e-6))
            {
                return placement;
            }

            double maxTravel = dx < 0.0 ? placement.X : placement.Y;
            if (maxTravel <= 1e-6)
            {
                return placement;
            }

            double low = 0.0;
            double high = maxTravel;
            for (int i = 0; i < 10; i++)
            {
                PumpUi(false);
                double mid = (low + high) / 2.0;
                ShapePlacement candidate = MakePlacement(placement.Part, template, placement.BoardIndex, placement.X + dx * mid, placement.Y + dy * mid);
                if (FitsBoard(input, candidate.Bounds) && !HitsAny(candidate, board))
                {
                    low = mid;
                }
                else
                {
                    high = mid;
                }
            }
            return MakePlacement(placement.Part, template, placement.BoardIndex, placement.X + dx * low, placement.Y + dy * low);
        }

        private static List<double> SettleAxisCandidates(ShapeNestingInput input, List<ShapePlacement> board, V4Template template, double current, double maxValue, bool xAxis)
        {
            List<double> values = new List<double>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            AddSettleAxis(values, seen, current);

            foreach (ShapePolygon moving in template.Polygons)
            {
                PumpUi(false);
                ShapeBounds mb = moving.Bounds();
                if (xAxis)
                {
                    AddSettleAxis(values, seen, -mb.MinX);
                    AddSettleAxis(values, seen, input.BoardWidth - mb.MaxX);
                }
                else
                {
                    AddSettleAxis(values, seen, -mb.MinY);
                    AddSettleAxis(values, seen, input.BoardHeight - mb.MaxY);
                }
            }

            foreach (ShapePlacement existing in board)
            {
                PumpUi(false);
                foreach (ShapePolygon fixedPolygon in ActualPolygons(existing))
                {
                    ShapeBounds fb = fixedPolygon.Bounds();
                    List<ShapePoint> fixedAnchors = LimitAnchors(HugAnchorPoints(fixedPolygon), 4);
                    foreach (ShapePolygon moving in template.Polygons)
                    {
                        ShapeBounds mb = moving.Bounds();
                        List<ShapePoint> movingAnchors = LimitAnchors(HugAnchorPoints(moving), 4);
                        if (xAxis)
                        {
                            AddSettleAxis(values, seen, fb.MaxX + Gap - mb.MinX);
                            AddSettleAxis(values, seen, fb.MinX - Gap - mb.MaxX);
                            AddSettleAxis(values, seen, fb.MinX - mb.MinX);
                            AddSettleAxis(values, seen, fb.MaxX - mb.MaxX);
                            foreach (ShapePoint fixedPoint in fixedAnchors)
                            {
                                foreach (ShapePoint movingPoint in movingAnchors)
                                {
                                    AddSettleAxis(values, seen, fixedPoint.X - movingPoint.X);
                                    AddSettleAxis(values, seen, fixedPoint.X - movingPoint.X + Gap);
                                    AddSettleAxis(values, seen, fixedPoint.X - movingPoint.X - Gap);
                                }
                            }
                        }
                        else
                        {
                            AddSettleAxis(values, seen, fb.MaxY + Gap - mb.MinY);
                            AddSettleAxis(values, seen, fb.MinY - Gap - mb.MaxY);
                            AddSettleAxis(values, seen, fb.MinY - mb.MinY);
                            AddSettleAxis(values, seen, fb.MaxY - mb.MaxY);
                            foreach (ShapePoint fixedPoint in fixedAnchors)
                            {
                                foreach (ShapePoint movingPoint in movingAnchors)
                                {
                                    AddSettleAxis(values, seen, fixedPoint.Y - movingPoint.Y);
                                    AddSettleAxis(values, seen, fixedPoint.Y - movingPoint.Y + Gap);
                                    AddSettleAxis(values, seen, fixedPoint.Y - movingPoint.Y - Gap);
                                }
                            }
                        }
                    }
                }
            }

            values.Sort();
            List<double> filtered = new List<double>();
            foreach (double value in values)
            {
                if (value < -1e-6 || value > maxValue + 1e-6)
                {
                    continue;
                }
                AddSettleAxis(filtered, null, Math.Max(0.0, value));
            }
            if (filtered.Count == 0)
            {
                filtered.Add(Math.Max(0.0, current));
            }
            return SelectSettleAxisCandidates(filtered, current);
        }

        private static List<double> SelectSettleAxisCandidates(List<double> values, double current)
        {
            if (values.Count <= SettleAxisLimit)
            {
                return values;
            }

            List<double> selected = new List<double>();
            AddSettleAxis(selected, null, 0.0);
            AddSettleAxis(selected, null, current);

            int lowTake = Math.Min(values.Count, SettleAxisLimit / 2);
            for (int i = 0; i < lowTake && selected.Count < SettleAxisLimit; i++)
            {
                AddSettleAxis(selected, null, values[i]);
            }

            int remaining = Math.Max(1, SettleAxisLimit - selected.Count);
            for (int bucket = 0; bucket < remaining && selected.Count < SettleAxisLimit; bucket++)
            {
                int index = (int)Math.Floor(values.Count * bucket / (double)remaining);
                if (index >= values.Count)
                {
                    index = values.Count - 1;
                }
                AddSettleAxis(selected, null, values[index]);
            }

            selected.Sort();
            return selected;
        }

        private static void AddSettleAxis(List<double> values, Dictionary<string, bool> seen, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return;
            }
            string key = ((int)Math.Round(value * 100.0)).ToString(CultureInfo.InvariantCulture);
            if (seen != null)
            {
                if (seen.ContainsKey(key))
                {
                    return;
                }
                seen.Add(key, true);
            }
            else
            {
                foreach (double existing in values)
                {
                    if (Math.Abs(existing - value) <= 0.01)
                    {
                        return;
                    }
                }
            }
            values.Add(value);
        }

        private static double BottomLeftSettleScore(List<ShapePlacement> board, ShapePlacement placement)
        {
            double contact = 0.0;
            foreach (ShapePlacement existing in board)
            {
                contact += BoundsContact(placement.Bounds, existing.Bounds, 5.0);
            }
            return placement.Bounds.MinY * 1000000000.0 +
                placement.Bounds.MinX * 10000000.0 +
                placement.Bounds.MaxY * 50000.0 +
                placement.Bounds.MaxX * 500.0 -
                contact * 250000.0;
        }

        private static ShapePlacement MakePlacement(ShapePartInstance part, V4Template template, int boardIndex, double x, double y)
        {
            ShapeBounds bounds = TranslateBounds(template.Bounds, x, y);
            return new ShapePlacement
            {
                Part = part,
                BoardIndex = boardIndex,
                X = x,
                Y = y,
                Rotation = 0,
                PlacedPolygon = BoundsPolygon(bounds),
                Bounds = bounds
            };
        }

        private static ShapePartInstance CreateGroupPart(ShapeNestingInput input, int typeIndex, V4Template template, int startNumber)
        {
            ShapePartType type = input.PartTypes[typeIndex];
            ShapePartInstance group = new ShapePartInstance();
            group.TypeIndex = typeIndex;
            group.Number = startNumber;
            group.Name = type.Name;
            group.Polygon = BoundsPolygon(template.Bounds);
            group.ColorIndex = type.ColorIndex;
            group.Components = new List<ShapePartComponent>();
            for (int i = 0; i < template.Polygons.Count; i++)
            {
                ShapePartInstance part = new ShapePartInstance
                {
                    TypeIndex = typeIndex,
                    Number = startNumber + i,
                    Name = type.Name,
                    Polygon = type.Polygon,
                    ColorIndex = type.ColorIndex
                };
                group.Components.Add(new ShapePartComponent { Part = part, LocalPolygon = template.Polygons[i] });
            }
            return group;
        }

        private static List<V4Strategy> BuildStrategies(ShapeNestingInput input, List<int> order, int generation)
        {
            List<V4Strategy> strategies = new List<V4Strategy>();
            List<int> rotations = new List<int> { 0, 90 };
            List<int> expansions = new List<int> { 1 };
            List<int> countModes = ShuffledValues(new int[] { 0, 1, 2, 3 }, generation, 37);
            List<int> shapeModes = ShuffledValues(new int[] { 0, 1, 2, 3 }, generation, 53);
            int variant = 0;
            foreach (int rotation in rotations)
            {
                foreach (int expansion in expansions)
                {
                    int countMode = countModes[variant % countModes.Count];
                    int shapeMode = shapeModes[(variant + generation) % shapeModes.Count];
                    string countName = CountModeName(countMode);
                    string shapeName = ShapeModeName(shapeMode);
                    strategies.Add(new V4Strategy
                    {
                        Name = "combo-" + (expansion == 1 ? "chain-x" : "chain-y") +
                            " " + countName + " " + shapeName +
                            " r" + rotation.ToString(CultureInfo.InvariantCulture),
                        MainRotation = rotation,
                        GroupSize = MaxComboBlockSize,
                        TemplateAreaWeight = 0.015,
                        ExpansionMode = expansion,
                        CountMode = countMode,
                        ShapeMode = shapeMode,
                        RandomSalt = unchecked(_runSeed + generation * 1009 + variant * 9176 + rotation * 31 + expansion),
                        RandomWeight = 90.0 + 35.0 * ((variant + generation) % 3),
                        ChoiceSpan = 2 + ((variant + generation) % 5)
                    });
                    variant++;
                }
            }
            AddExtraExplorationStrategies(strategies, generation, countModes, shapeModes);
            return strategies;
        }

        private static void AddExtraExplorationStrategies(List<V4Strategy> strategies, int generation, List<int> countModes, List<int> shapeModes)
        {
            int target = 8;
            int variant = strategies.Count;
            while (strategies.Count < target)
            {
                int countMode = countModes[(variant + 1) % countModes.Count];
                int shapeMode = shapeModes[(variant + 2) % shapeModes.Count];
                int rotation = PositiveModulo(_runSeed + generation * 17 + variant * 31, 2) == 0 ? 0 : 90;
                int expansion = PositiveModulo(_runSeed + generation * 29 + variant * 13, 2) == 0 ? 1 : 2;
                strategies.Add(new V4Strategy
                {
                    Name = "combo-explore-" + CountModeName(countMode) + " " + ShapeModeName(shapeMode) +
                        " r" + rotation.ToString(CultureInfo.InvariantCulture),
                    MainRotation = rotation,
                    GroupSize = MaxComboBlockSize,
                    TemplateAreaWeight = 0.015,
                    ExpansionMode = expansion,
                    CountMode = countMode,
                    ShapeMode = shapeMode,
                    RandomSalt = unchecked(_runSeed + generation * 1543 + variant * 7919),
                    RandomWeight = 160.0,
                    ChoiceSpan = 4 + (variant % 5)
                });
                variant++;
            }
        }

        private static List<int> ShuffledValues(int[] values, int generation, int salt)
        {
            List<int> result = new List<int>(values);
            result.Sort(delegate (int a, int b)
            {
                int ah = DeterministicHash(_runSeed, generation, salt, a);
                int bh = DeterministicHash(_runSeed, generation, salt, b);
                return ah.CompareTo(bh);
            });
            return result;
        }

        private static int DeterministicHash(int seed, int generation, int salt, int value)
        {
            int hash = seed;
            hash = unchecked(hash * 16777619) ^ generation;
            hash = unchecked(hash * 16777619) ^ salt;
            hash = unchecked(hash * 16777619) ^ value;
            return unchecked((int)(PositiveUnitHash(hash) * int.MaxValue));
        }

        private static int PositiveModulo(int value, int modulus)
        {
            int result = value % modulus;
            return result < 0 ? result + modulus : result;
        }

        private static string CountModeName(int mode)
        {
            if (mode == 1)
            {
                return "x4-x6-x2-x1";
            }
            if (mode == 2)
            {
                return "x2-x4-x6-x1";
            }
            if (mode == 3)
            {
                return "x6-x2-x4-x1";
            }
            return "x6-x4-x2-x1";
        }

        private static string ShapeModeName(int mode)
        {
            if (mode == 1)
            {
                return "block-first";
            }
            if (mode == 2)
            {
                return "chain-first";
            }
            if (mode == 3)
            {
                return "large-box-first";
            }
            return "score-first";
        }

        private static List<int> TypeOrder(ShapeNestingInput input)
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
                double av = Math.Max(ab.Width, ab.Height);
                double bv = Math.Max(bb.Width, bb.Height);
                int cmp = bv.CompareTo(av);
                if (cmp != 0)
                {
                    return cmp;
                }
                double aa = input.PartTypes[a].Polygon.Area();
                double ba = input.PartTypes[b].Polygon.Area();
                cmp = ba.CompareTo(aa);
                if (cmp != 0)
                {
                    return cmp;
                }
                return b.CompareTo(a);
            });
            return order;
        }

        private static string FormatTypeOrder(ShapeNestingInput input, List<int> order)
        {
            List<string> names = new List<string>();
            foreach (int index in order)
            {
                ShapePartType type = input.PartTypes[index];
                names.Add(type.Name + "(" + type.Polygon.Area().ToString("0.#", CultureInfo.InvariantCulture) + " x " + type.Quantity.ToString(CultureInfo.InvariantCulture) + ")");
            }
            return string.Join(" > ", names.ToArray());
        }

        private static V4Score Score(ShapeNestingResult result)
        {
            return new V4Score
            {
                BoardCount = result.BoardCount,
                AreaUtilization = result.Utilization,
                LastRight = result.MinRightRemnant,
                TotalRight = result.TotalRightRemnant,
                Envelope = EnvelopeArea(result)
            };
        }

        private static int Compare(V4Score a, V4Score b)
        {
            if (a.BoardCount != b.BoardCount)
            {
                return a.BoardCount.CompareTo(b.BoardCount);
            }
            int cmp = CompareDescending(a.AreaUtilization, b.AreaUtilization, 1e-6);
            if (cmp != 0)
            {
                return cmp;
            }
            cmp = CompareDescending(a.LastRight, b.LastRight, 1e-6);
            if (cmp != 0)
            {
                return cmp;
            }
            cmp = CompareDescending(a.TotalRight, b.TotalRight, 1e-6);
            if (cmp != 0)
            {
                return cmp;
            }
            return a.Envelope.CompareTo(b.Envelope);
        }

        private static int CompareDescending(double a, double b, double tolerance)
        {
            if (Math.Abs(a - b) <= tolerance)
            {
                return 0;
            }
            return b.CompareTo(a);
        }

        private static int CompareAscending(double a, double b, double tolerance)
        {
            if (Math.Abs(a - b) <= tolerance)
            {
                return 0;
            }
            return a.CompareTo(b);
        }

        private static void SettleBoardsBottomLeft(ShapeNestingInput input, List<List<ShapePlacement>> boards)
        {
            List<List<ShapePlacement>> original = CopyBoards(boards);
            V4Score originalScore = ScoreBoards(input, original);
            double originalTopOpen = BoardTopOpenArea(input, original);

            SettleBoardsBottomLeftCore(input, boards);
            CompactBoards(boards);

            V4Score settledScore = ScoreBoards(input, boards);
            double settledTopOpen = BoardTopOpenArea(input, boards);
            if (!AcceptSettledBoards(input, originalScore, settledScore, originalTopOpen, settledTopOpen))
            {
                RestoreBoards(boards, original);
            }
        }

        private static bool AcceptSettledBoards(ShapeNestingInput input, V4Score original, V4Score settled, double originalTopOpen, double settledTopOpen)
        {
            if (settled.BoardCount > original.BoardCount)
            {
                return false;
            }

            double originalUsed = input.BoardWidth * original.BoardCount - original.TotalRight;
            double settledUsed = input.BoardWidth * settled.BoardCount - settled.TotalRight;
            if (settledUsed > originalUsed + 0.5)
            {
                return false;
            }
            if (settled.AreaUtilization + 1e-6 < original.AreaUtilization)
            {
                return false;
            }

            if (settledUsed < originalUsed - 0.5)
            {
                return true;
            }
            if (settledTopOpen + input.BoardWidth * input.BoardHeight * 0.003 < originalTopOpen)
            {
                return false;
            }
            return Compare(settled, original) <= 0 || settledTopOpen > originalTopOpen + input.BoardWidth;
        }

        private static double BoardTopOpenArea(ShapeNestingInput input, List<List<ShapePlacement>> boards)
        {
            double area = 0.0;
            foreach (List<ShapePlacement> board in boards)
            {
                area += BoardTopOpenArea(input, board);
            }
            return area;
        }

        private static double BoardTopOpenArea(ShapeNestingInput input, List<ShapePlacement> board)
        {
            if (board == null || board.Count == 0)
            {
                return input.BoardWidth * input.BoardHeight;
            }

            double usedX = Math.Min(input.BoardWidth, UsedX(board));
            if (usedX <= 1e-6)
            {
                return input.BoardWidth * input.BoardHeight;
            }

            List<double> xs = new List<double>();
            AddOpenSpaceAxis(xs, 0.0);
            AddOpenSpaceAxis(xs, usedX);
            foreach (ShapePlacement placement in board)
            {
                AddOpenSpaceAxis(xs, Math.Max(0.0, Math.Min(usedX, placement.Bounds.MinX)));
                AddOpenSpaceAxis(xs, Math.Max(0.0, Math.Min(usedX, placement.Bounds.MaxX)));
            }
            xs.Sort();

            double open = Math.Max(0.0, input.BoardWidth - usedX) * input.BoardHeight;
            for (int i = 0; i < xs.Count - 1; i++)
            {
                double minX = xs[i];
                double maxX = xs[i + 1];
                double width = maxX - minX;
                if (width <= 1e-6)
                {
                    continue;
                }

                double maxY = 0.0;
                foreach (ShapePlacement placement in board)
                {
                    if (placement.Bounds.MaxX <= minX + 1e-6 || placement.Bounds.MinX >= maxX - 1e-6)
                    {
                        continue;
                    }
                    maxY = Math.Max(maxY, placement.Bounds.MaxY);
                }
                open += width * Math.Max(0.0, input.BoardHeight - maxY);
            }
            return open;
        }

        private static void AddOpenSpaceAxis(List<double> values, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return;
            }
            foreach (double existing in values)
            {
                if (Math.Abs(existing - value) <= 0.01)
                {
                    return;
                }
            }
            values.Add(value);
        }

        private static void SettleBoardsBottomLeftCore(ShapeNestingInput input, List<List<ShapePlacement>> boards)
        {
            for (int boardIndex = 0; boardIndex < boards.Count; boardIndex++)
            {
                List<ShapePlacement> board = boards[boardIndex];
                for (int pass = 0; pass < BoardSettlePassLimit; pass++)
                {
                    bool movedAny = false;
                    board.Sort(delegate (ShapePlacement a, ShapePlacement b)
                    {
                        int cmp = a.Bounds.MinY.CompareTo(b.Bounds.MinY);
                        return cmp != 0 ? cmp : a.Bounds.MinX.CompareTo(b.Bounds.MinX);
                    });
                    for (int i = 0; i < board.Count; i++)
                    {
                        ShapePlacement current = board[i];
                        V4Template template = TemplateFromPlacement(current);
                        board.RemoveAt(i);
                        ShapePlacement seed = MakePlacement(current.Part, template, boardIndex, current.Bounds.MinX, current.Bounds.MinY);
                        ShapePlacement settled = GlobalSettlePlacementBottomLeft(input, board, seed, template);
                        settled.BoardIndex = boardIndex;
                        if (Math.Abs(settled.X - current.X) > 0.01 || Math.Abs(settled.Y - current.Y) > 0.01)
                        {
                            movedAny = true;
                        }
                        board.Insert(i, settled);
                    }
                    if (!movedAny)
                    {
                        break;
                    }
                }
            }
        }

        private static ShapePlacement GlobalSettlePlacementBottomLeft(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement placement, V4Template template)
        {
            ShapePlacement current = placement;
            for (int pass = 0; pass < PlacementTightLoopLimit; pass++)
            {
                ShapePlacement before = current;
                ShapePlacement best = current;
                double bestScore = FlowSettleScore(input, board, placement, current);

                ShapePlacement nfpFirst = FullSettlePlacementBottomLeft(input, board, current, template);
                KeepBetterFlowSettle(input, board, placement, nfpFirst, ref best, ref bestScore);

                ShapePlacement leftFirst = SlidePlacement(input, board, current, template, -1.0, 0.0);
                leftFirst = SlidePlacement(input, board, leftFirst, template, 0.0, -1.0);
                leftFirst = SlidePlacement(input, board, leftFirst, template, -1.0, 0.0);
                KeepBetterFlowSettle(input, board, placement, leftFirst, ref best, ref bestScore);

                ShapePlacement downFirst = SlidePlacement(input, board, current, template, 0.0, -1.0);
                downFirst = SlidePlacement(input, board, downFirst, template, -1.0, 0.0);
                downFirst = SlidePlacement(input, board, downFirst, template, 0.0, -1.0);
                KeepBetterFlowSettle(input, board, placement, downFirst, ref best, ref bestScore);

                ShapePlacement leftOnly = SlidePlacement(input, board, current, template, -1.0, 0.0);
                KeepBetterFlowSettle(input, board, placement, leftOnly, ref best, ref bestScore);

                ShapePlacement downOnly = SlidePlacement(input, board, current, template, 0.0, -1.0);
                KeepBetterFlowSettle(input, board, placement, downOnly, ref best, ref bestScore);

                if ((board.Count <= 36 || IsFrontierPlacement(board, placement)) && best == current)
                {
                    ShapePlacement nfp = FullSettlePlacementBottomLeft(input, board, best, template);
                    KeepBetterFlowSettle(input, board, placement, nfp, ref best, ref bestScore);
                }

                current = best;
                if (Math.Abs(current.X - before.X) < 0.01 && Math.Abs(current.Y - before.Y) < 0.01)
                {
                    break;
                }
            }
            return current;
        }

        private static List<ShapePolygon> NfpSettleMovingGroup(ShapeNestingInput input, List<ShapePolygon> placed, List<ShapePolygon> moving)
        {
            List<ShapePoint> candidates = NfpGroupCandidateOffsets(placed, NormalizePolygons(moving), GroupCandidateLimit);
            if (candidates.Count == 0)
            {
                return null;
            }

            List<ShapePolygon> normalized = NormalizePolygons(moving);
            List<ShapePolygon> best = null;
            double bestScore = double.PositiveInfinity;
            foreach (ShapePoint point in candidates)
            {
                PumpUi(false);
                List<ShapePolygon> moved = TranslatePolygons(normalized, point.X, point.Y);
                ShapeBounds bounds = BoundsOf(moved);
                if (bounds.MinX < -1e-6 || bounds.MinY < -1e-6 ||
                    bounds.MaxX > input.BoardWidth + 1e-6 || bounds.MaxY > input.BoardHeight + 1e-6 ||
                    GroupPolygonsOverlap(moved, placed))
                {
                    continue;
                }
                List<ShapePolygon> all = new List<ShapePolygon>(placed);
                all.AddRange(moved);
                ShapeBounds allBounds = BoundsOf(all);
                double score = allBounds.Width * allBounds.Height +
                    allBounds.MaxY * 200.0 +
                    allBounds.MaxX * 80.0 -
                    GroupContactReward(moved, placed) * 600.0 -
                    GroupNearReward(moved, placed) * 300.0;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = moved;
                }
            }
            return best;
        }

        private static void KeepBetterFlowSettle(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement original, ShapePlacement candidate, ref ShapePlacement best, ref double bestScore)
        {
            if (candidate == null || !AcceptPlacementSettle(input, board, original, candidate))
            {
                return;
            }
            double score = FlowSettleScore(input, board, original, candidate);
            if (score < bestScore - 0.01)
            {
                bestScore = score;
                best = candidate;
            }
        }

        private static bool AcceptPlacementSettle(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement original, ShapePlacement candidate)
        {
            if (!FitsBoard(input, candidate.Bounds) || HitsAny(candidate, board))
            {
                return false;
            }

            double boardUsedWithout = UsedX(board);
            double originalUsed = Math.Max(boardUsedWithout, original.Bounds.MaxX);
            double candidateUsed = Math.Max(boardUsedWithout, candidate.Bounds.MaxX);
            if (candidateUsed > originalUsed + 0.25)
            {
                return false;
            }

            bool reducesUsed = candidateUsed < originalUsed - 0.25;
            if (!reducesUsed && candidate.Bounds.MaxX > original.Bounds.MaxX + Math.Max(4.0, original.Bounds.Width * 0.08))
            {
                return false;
            }
            if (!reducesUsed && candidate.Bounds.MaxY > original.Bounds.MaxY + Math.Max(4.0, original.Bounds.Height * 0.08))
            {
                return false;
            }
            return true;
        }

        private static bool IsFrontierPlacement(List<ShapePlacement> board, ShapePlacement placement)
        {
            double boardUsedWithout = UsedX(board);
            return placement.Bounds.MaxX >= boardUsedWithout - 0.25;
        }

        private static double FlowSettleScore(ShapeNestingInput input, List<ShapePlacement> board, ShapePlacement original, ShapePlacement candidate)
        {
            double boardUsedWithout = UsedX(board);
            double originalUsed = Math.Max(boardUsedWithout, original.Bounds.MaxX);
            double candidateUsed = Math.Max(boardUsedWithout, candidate.Bounds.MaxX);
            double usedIncrease = Math.Max(0.0, candidateUsed - originalUsed);
            double usedReduction = Math.Max(0.0, originalUsed - candidateUsed);
            bool frontier = IsFrontierPlacement(board, original);
            double contact = FastBoundsContactReward(board, candidate.Bounds);
            double near = FastBoundsNearReward(board, candidate.Bounds, 55.0);

            double score = usedIncrease * input.BoardWidth * input.BoardHeight * 10000.0 -
                usedReduction * input.BoardHeight * 650000.0;

            if (frontier)
            {
                score += candidateUsed * 900000000.0 +
                    candidate.Bounds.MaxX * 520000.0 +
                    candidate.Bounds.MinX * 150000.0 +
                    candidate.Bounds.MinY * 65000.0 +
                    candidate.Bounds.MaxY * 12000.0;
            }
            else
            {
                double topOpen = Math.Max(0.0, input.BoardHeight - candidate.Bounds.MaxY);
                score += candidateUsed * 120000000.0 +
                    candidate.Bounds.MaxY * 650000.0 +
                    candidate.Bounds.MinY * 360000.0 +
                    candidate.Bounds.MinX * 45000.0 +
                    candidate.Bounds.MaxX * 9000.0 -
                    topOpen * Math.Max(1.0, candidate.Bounds.Width) * 22000.0;
            }

            score -= contact * 700000.0;
            score -= near * 360000.0;
            return score;
        }

        private static V4Template TemplateFromPlacement(ShapePlacement placement)
        {
            List<ShapePolygon> locals = new List<ShapePolygon>();
            foreach (ShapePolygon polygon in ActualPolygons(placement))
            {
                locals.Add(polygon.Translate(-placement.X, -placement.Y));
            }
            V4Template template = NormalizeTemplate(locals);
            template.ShelfLayout = false;
            return template;
        }

        private static void RebuildPlacements(ShapeNestingResult result, List<List<ShapePlacement>> boards)
        {
            result.Placements.Clear();
            for (int boardIndex = 0; boardIndex < boards.Count; boardIndex++)
            {
                foreach (ShapePlacement placement in boards[boardIndex])
                {
                    placement.BoardIndex = boardIndex;
                    result.Placements.Add(placement);
                }
            }
        }

        private static void FinalizeResult(ShapeNestingInput input, ShapeNestingResult result, List<List<ShapePlacement>> boards)
        {
            int boardCount = 0;
            double usedSum = 0.0;
            double lastUsed = 0.0;
            for (int i = 0; i < boards.Count; i++)
            {
                if (boards[i].Count == 0)
                {
                    continue;
                }
                boardCount++;
                lastUsed = UsedX(boards[i]);
                usedSum += lastUsed;
            }
            result.BoardCount = Math.Max(1, boardCount);
            result.MinRightRemnant = Math.Max(0.0, input.BoardWidth - lastUsed);
            result.TotalRightRemnant = input.BoardWidth * result.BoardCount - usedSum;
            result.Utilization = PlacedPartArea(boards) / Math.Max(1.0, usedSum * input.BoardHeight);
        }

        private static void TryRepackTrailingTypes(ShapeNestingInput input, List<List<ShapePlacement>> boards, List<int> order, V4Strategy strategy, Dictionary<string, List<V4Template>> templateCache)
        {
            if (input == null || boards == null || order == null || order.Count < 2)
            {
                return;
            }

            List<List<ShapePlacement>> original = CopyBoards(boards);
            V4Score originalScore = ScoreBoards(input, original);
            if (!RepackTypesFromOrderIndex(input, boards, order, 1, strategy, templateCache))
            {
                RestoreBoards(boards, original);
                return;
            }

            CompactBoards(boards);
            V4Score repackedScore = ScoreBoards(input, boards);
            if (Compare(repackedScore, originalScore) >= 0)
            {
                RestoreBoards(boards, original);
            }
        }

        private static bool RepackTypesFromOrderIndex(ShapeNestingInput input, List<List<ShapePlacement>> boards, List<int> order, int startOrderIndex, V4Strategy strategy, Dictionary<string, List<V4Template>> templateCache)
        {
            int[] remaining = new int[input.PartTypes.Count];
            int[] nextNumber = InitialNumbers(input);
            Dictionary<int, bool> repackTypes = new Dictionary<int, bool>();
            for (int i = startOrderIndex; i < order.Count; i++)
            {
                repackTypes[order[i]] = true;
            }

            for (int boardIndex = 0; boardIndex < boards.Count; boardIndex++)
            {
                List<ShapePlacement> kept = new List<ShapePlacement>();
                foreach (ShapePlacement placement in boards[boardIndex])
                {
                    int typeIndex = placement.Part.TypeIndex;
                    if (repackTypes.ContainsKey(typeIndex))
                    {
                        remaining[typeIndex] += CountParts(placement);
                    }
                    else
                    {
                        kept.Add(placement);
                    }
                }
                boards[boardIndex] = kept;
            }

            for (int orderIndex = startOrderIndex; orderIndex < order.Count; orderIndex++)
            {
                int typeIndex = order[orderIndex];
                while (remaining[typeIndex] > 0)
                {
                    PumpUi(false);
                    ShapePlacement placement = BestRepackPlacement(input, boards, typeIndex, remaining[typeIndex], nextNumber[typeIndex], strategy, templateCache);
                    if (placement == null)
                    {
                        return false;
                    }
                    boards[placement.BoardIndex].Add(placement);
                    remaining[typeIndex] -= CountParts(placement);
                    nextNumber[typeIndex] += CountParts(placement);
                }
            }
            return true;
        }

        private static ShapePlacement BestRepackPlacement(ShapeNestingInput input, List<List<ShapePlacement>> boards, int typeIndex, int remaining, int nextNumber, V4Strategy strategy, Dictionary<string, List<V4Template>> templateCache)
        {
            ShapePlacement best = null;
            double bestScore = double.PositiveInfinity;
            List<int> counts = PreferredCountsFromMax(remaining, Math.Min(MaxComboBlockSize, remaining), PreferPairUnits(remaining));
            bool fastPairCounts = PreferPairUnits(remaining);
            foreach (int count in counts)
            {
                foreach (V4Template template in BuildTemplatesForCountFast(input, typeIndex, count, strategy, templateCache, fastPairCounts))
                {
                    foreach (V4Template placementTemplate in PlacementTemplateVariants(template))
                    {
                        if (placementTemplate.Bounds.Width > input.BoardWidth + 1e-6 || placementTemplate.Bounds.Height > input.BoardHeight + 1e-6)
                        {
                            continue;
                        }

                        for (int boardIndex = 0; boardIndex < boards.Count; boardIndex++)
                        {
                            List<ShapePlacement> board = boards[boardIndex];
                            if (board.Count == 0)
                            {
                                continue;
                            }
                            ShapePartInstance part = CreateGroupPart(input, typeIndex, placementTemplate, nextNumber);
                            foreach (ShapePoint point in RepackCandidatePoints(input, board, placementTemplate))
                            {
                                PumpUi(false);
                                ShapePlacement seed = MakePlacement(part, placementTemplate, boardIndex, point.X, point.Y);
                                ShapePlacement placement = SettlePlacementBottomLeft(input, board, seed, placementTemplate);
                                if (!FitsBoard(input, placement.Bounds) || HitsAny(placement, board))
                                {
                                    continue;
                                }

                                double score = RepackPlacementScore(input, boards, boardIndex, board, placement);
                                if (score < bestScore)
                                {
                                    bestScore = score;
                                    best = placement;
                                }
                            }
                        }
                    }
                }
            }
            if (best == null || best.BoardIndex < 0 || best.BoardIndex >= boards.Count)
            {
                return best;
            }
            return PolishSelectedPlacement(input, boards[best.BoardIndex], best);
        }

        private static List<ShapePoint> RepackCandidatePoints(ShapeNestingInput input, List<ShapePlacement> board, V4Template template)
        {
            List<ShapePoint> result = new List<ShapePoint>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            foreach (ShapePoint point in CandidatePoints(input, board, template, true))
            {
                AddCandidate(result, seen, point.X, point.Y);
            }
            double usedX = UsedX(board);
            foreach (ShapePoint point in BandCandidatePoints(input, board, template, usedX))
            {
                AddCandidate(result, seen, point.X, point.Y);
            }
            return LimitPoints(result, FillCandidateLimit);
        }

        private static double RepackPlacementScore(ShapeNestingInput input, List<List<ShapePlacement>> boards, int boardIndex, List<ShapePlacement> board, ShapePlacement placement)
        {
            double oldUsed = UsedX(board);
            double addedX = Math.Max(0.0, placement.Bounds.MaxX - oldUsed);
            double usedSumAfter = UsedSumWithPlacement(boards, boardIndex, placement);
            double contact = FastBoundsContactReward(board, placement.Bounds);
            double near = FastBoundsNearReward(board, placement.Bounds, 80.0);
            return usedSumAfter * 1000000.0 +
                addedX * 14000000.0 +
                placement.Bounds.MaxX * 6000.0 +
                placement.Bounds.MinY * 1200.0 +
                placement.Bounds.MinX * 500.0 -
                CountParts(placement) * input.BoardWidth * input.BoardHeight * 45.0 -
                contact * 1100000.0 -
                near * 300000.0;
        }

        private static double UsedSumWithPlacement(List<List<ShapePlacement>> boards, int placementBoardIndex, ShapePlacement placement)
        {
            double used = 0.0;
            for (int i = 0; i < boards.Count; i++)
            {
                double boardUsed = UsedX(boards[i]);
                if (i == placementBoardIndex)
                {
                    boardUsed = Math.Max(boardUsed, placement.Bounds.MaxX);
                }
                used += boardUsed;
            }
            return used;
        }

        private static V4Score ScoreBoards(ShapeNestingInput input, List<List<ShapePlacement>> boards)
        {
            int boardCount = 0;
            double usedSum = 0.0;
            double lastUsed = 0.0;
            foreach (List<ShapePlacement> board in boards)
            {
                if (board.Count == 0)
                {
                    continue;
                }
                boardCount++;
                lastUsed = UsedX(board);
                usedSum += lastUsed;
            }
            return new V4Score
            {
                BoardCount = Math.Max(1, boardCount),
                AreaUtilization = PlacedPartArea(boards) / Math.Max(1.0, usedSum * input.BoardHeight),
                LastRight = Math.Max(0.0, input.BoardWidth - lastUsed),
                TotalRight = input.BoardWidth * Math.Max(1, boardCount) - usedSum,
                Envelope = BoardEnvelopeArea(boards)
            };
        }

        private static double BoardEnvelopeArea(List<List<ShapePlacement>> boards)
        {
            double area = 0.0;
            foreach (List<ShapePlacement> board in boards)
            {
                double maxX = 0.0;
                double maxY = 0.0;
                foreach (ShapePlacement placement in board)
                {
                    maxX = Math.Max(maxX, placement.Bounds.MaxX);
                    maxY = Math.Max(maxY, placement.Bounds.MaxY);
                }
                area += maxX * maxY;
            }
            return area;
        }

        private static List<List<ShapePlacement>> CopyBoards(List<List<ShapePlacement>> boards)
        {
            List<List<ShapePlacement>> copy = new List<List<ShapePlacement>>();
            foreach (List<ShapePlacement> board in boards)
            {
                copy.Add(new List<ShapePlacement>(board));
            }
            return copy;
        }

        private static void RestoreBoards(List<List<ShapePlacement>> boards, List<List<ShapePlacement>> original)
        {
            boards.Clear();
            foreach (List<ShapePlacement> board in original)
            {
                boards.Add(new List<ShapePlacement>(board));
            }
        }

        private static double UsedLengthRatio(ShapeNestingResult result)
        {
            if (result == null || result.Input == null || result.BoardCount <= 0)
            {
                return 0.0;
            }
            double used = result.Input.BoardWidth * result.BoardCount - result.TotalRightRemnant;
            return used / Math.Max(1.0, result.Input.BoardWidth * result.BoardCount);
        }

        private static double PlacedPartArea(List<List<ShapePlacement>> boards)
        {
            double area = 0.0;
            foreach (List<ShapePlacement> board in boards)
            {
                foreach (ShapePlacement placement in board)
                {
                    foreach (ShapePolygon polygon in ActualPolygons(placement))
                    {
                        area += Math.Abs(polygon.Area());
                    }
                }
            }
            return area;
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
            double area = 0.0;
            for (int i = 0; i < maxX.Length; i++)
            {
                area += maxX[i] * maxY[i];
            }
            return area;
        }

        private static bool FitsBoard(ShapeNestingInput input, ShapeBounds bounds)
        {
            return bounds.MinX >= -1e-6 && bounds.MinY >= -1e-6 && bounds.MaxX <= input.BoardWidth + 1e-6 && bounds.MaxY <= input.BoardHeight + 1e-6;
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

        private static List<ShapePolygon> ActualPolygons(ShapePlacement placement)
        {
            if (placement.ActualPolygons == null)
            {
                placement.ActualPolygons = ActualPolygons(placement.Part, placement.X, placement.Y);
            }
            return placement.ActualPolygons;
        }

        private static List<ShapeBounds> ActualPolygonBounds(ShapePlacement placement)
        {
            if (placement.ActualPolygonBounds == null)
            {
                placement.ActualPolygonBounds = PolygonBounds(ActualPolygons(placement));
            }
            return placement.ActualPolygonBounds;
        }

        private static List<ShapeBounds> PolygonBounds(List<ShapePolygon> polygons)
        {
            List<ShapeBounds> bounds = new List<ShapeBounds>();
            foreach (ShapePolygon polygon in polygons)
            {
                bounds.Add(polygon.Bounds());
            }
            return bounds;
        }

        private static bool Overlaps(ShapePolygon polygon, List<ShapePolygon> polygons)
        {
            ShapeBounds ab = polygon.Bounds();
            foreach (ShapePolygon existing in polygons)
            {
                ShapeBounds bb = existing.Bounds();
                if (ShapeCollision.BoundsOverlap(ab, bb) && ShapeCollision.Intersects(polygon, ab, existing, bb))
                {
                    return true;
                }
            }
            return false;
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

        private static double UsedX(List<ShapePlacement> board)
        {
            double used = 0.0;
            foreach (ShapePlacement placement in board)
            {
                used = Math.Max(used, placement.Bounds.MaxX);
            }
            return used;
        }

        private static double UsedY(List<ShapePlacement> board)
        {
            double used = 0.0;
            foreach (ShapePlacement placement in board)
            {
                used = Math.Max(used, placement.Bounds.MaxY);
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

        private static int[] Quantities(ShapeNestingInput input)
        {
            int[] values = new int[input.PartTypes.Count];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = input.PartTypes[i].Quantity;
            }
            return values;
        }

        private static int[] InitialNumbers(ShapeNestingInput input)
        {
            int[] values = new int[input.PartTypes.Count];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = 1;
            }
            return values;
        }

        private static int CountParts(ShapePlacement placement)
        {
            if (placement.Part.Components == null || placement.Part.Components.Count == 0)
            {
                return 1;
            }
            return placement.Part.Components.Count;
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

        private static void AddCandidate(List<ShapePoint> points, Dictionary<string, bool> seen, double x, double y)
        {
            if (x < -1e-6 || y < -1e-6)
            {
                return;
            }
            string key = ((int)Math.Round(x * 100.0)).ToString(CultureInfo.InvariantCulture) + "," + ((int)Math.Round(y * 100.0)).ToString(CultureInfo.InvariantCulture);
            if (seen.ContainsKey(key))
            {
                return;
            }
            seen.Add(key, true);
            points.Add(new ShapePoint(x, y));
        }

        private static List<ShapePoint> LimitPoints(List<ShapePoint> points, int limit)
        {
            points.Sort(delegate (ShapePoint a, ShapePoint b)
            {
                int cmp = a.Y.CompareTo(b.Y);
                return cmp != 0 ? cmp : a.X.CompareTo(b.X);
            });
            if (points.Count <= limit)
            {
                return points;
            }
            List<ShapePoint> selected = new List<ShapePoint>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            int buckets = Math.Min(24, points.Count);
            for (int bucket = 0; bucket < buckets && selected.Count < limit; bucket++)
            {
                int start = (int)Math.Floor(points.Count * bucket / (double)buckets);
                int end = (int)Math.Floor(points.Count * (bucket + 1) / (double)buckets);
                int take = Math.Min(end, start + Math.Max(1, limit / buckets));
                for (int i = start; i < take && i < points.Count && selected.Count < limit; i++)
                {
                    AddSelectedTemplatePoint(selected, seen, points[i]);
                }
            }
            for (int i = 0; i < points.Count && selected.Count < limit; i++)
            {
                AddSelectedTemplatePoint(selected, seen, points[i]);
            }
            selected.Sort(delegate (ShapePoint a, ShapePoint b)
            {
                int cmp = a.Y.CompareTo(b.Y);
                return cmp != 0 ? cmp : a.X.CompareTo(b.X);
            });
            return selected;
        }

        private static string TemplateKey(V4Template template)
        {
            List<string> parts = new List<string>();
            foreach (ShapePolygon polygon in template.Polygons)
            {
                List<string> points = new List<string>();
                foreach (ShapePoint point in polygon.Points)
                {
                    points.Add(
                        ((int)Math.Round(point.X * 10.0)).ToString(CultureInfo.InvariantCulture) + "," +
                        ((int)Math.Round(point.Y * 10.0)).ToString(CultureInfo.InvariantCulture));
                }
                parts.Add(string.Join(";", points.ToArray()));
            }
            parts.Sort();
            return template.Count.ToString(CultureInfo.InvariantCulture) + ":" +
                ((int)Math.Round(template.Bounds.Width * 10.0)).ToString(CultureInfo.InvariantCulture) + ":" +
                ((int)Math.Round(template.Bounds.Height * 10.0)).ToString(CultureInfo.InvariantCulture) + ":" +
                string.Join("|", parts.ToArray());
        }

        private static double TemplateScore(V4Template template, V4Strategy strategy)
        {
            double score = PairOrientationPenalty(template, strategy) * 120000.0 +
                template.Bounds.Width * template.Bounds.Height + Math.Max(template.Bounds.Width, template.Bounds.Height) * 180.0 + template.Count * 25.0;
            foreach (ShapePolygon polygon in template.Polygons)
            {
                ShapeBounds b = polygon.Bounds();
                score += Math.Max(b.Width, b.Height) * 6.0;
            }
            return score;
        }

        private static double PairOrientationPenalty(V4Template template, V4Strategy strategy)
        {
            if (template.PairRotation < 0)
            {
                return 0.0;
            }
            int preferred = NormalizeRotation(strategy.MainRotation) == 90 ? 90 : 0;
            return NormalizeRotation(template.PairRotation) == preferred ? 0.0 : 1.0;
        }

        private static void AddNewBoard(List<List<ShapePlacement>> boards)
        {
            boards.Add(new List<ShapePlacement>());
        }

        private static void AddHugGalleryTemplates(ShapeNestingInput input, int typeIndex, List<V4Template> templates, string reason)
        {
            if (_previewUpdated == null || templates == null || templates.Count == 0)
            {
                return;
            }

            bool added = false;
            foreach (V4Template template in templates)
            {
                if (template == null || template.Count <= 1 || template.Count % 2 != 0)
                {
                    continue;
                }
                string key = typeIndex.ToString(CultureInfo.InvariantCulture) + ":" + TemplateKey(template);
                if (_hugGallerySeen.ContainsKey(key))
                {
                    continue;
                }
                _hugGallerySeen[key] = true;
                _hugGalleryTemplates.Add(template);
                _hugGalleryTypes.Add(typeIndex);
                added = true;
            }

            if (added)
            {
                _nextPreviewDraw = DateTime.MinValue;
                PreviewTemplates(input, _hugGalleryTemplates, _hugGalleryTypes, reason);
            }
        }

        private static void PreviewTemplate(ShapeNestingInput input, int typeIndex, V4Template template, string reason)
        {
            if (_previewUpdated == null || template == null)
            {
                return;
            }
            PreviewTemplates(input, typeIndex, new List<V4Template> { template }, reason);
        }

        private static void ForcePreviewTemplate(ShapeNestingInput input, int typeIndex, V4Template template, string reason)
        {
            _nextPreviewDraw = DateTime.MinValue;
            PreviewTemplate(input, typeIndex, template, reason);
        }

        private static void ForcePreviewTemplates(ShapeNestingInput input, int typeIndex, List<V4Template> templates, string reason)
        {
            _nextPreviewDraw = DateTime.MinValue;
            PreviewTemplates(input, typeIndex, templates, reason);
        }

        private static void ForcePreviewTemplates(ShapeNestingInput input, List<V4Template> templates, List<int> typeIndices, string reason)
        {
            _nextPreviewDraw = DateTime.MinValue;
            PreviewTemplates(input, templates, typeIndices, reason);
        }

        private static void PreviewTemplates(ShapeNestingInput input, int typeIndex, List<V4Template> templates, string reason)
        {
            List<int> typeIndices = new List<int>();
            if (templates != null)
            {
                for (int i = 0; i < templates.Count; i++)
                {
                    typeIndices.Add(typeIndex);
                }
            }
            PreviewTemplates(input, templates, typeIndices, reason);
        }

        private static void PreviewTemplates(ShapeNestingInput input, List<V4Template> templates, List<int> typeIndices, string reason)
        {
            if (_previewUpdated == null || templates == null || templates.Count == 0)
            {
                return;
            }
            DateTime now = DateTime.UtcNow;
            if (now < _nextPreviewDraw)
            {
                return;
            }
            _nextPreviewDraw = now.AddMilliseconds(2000);
            try
            {
                List<V4Template> drawTemplates = new List<V4Template>();
                List<int> drawTypes = new List<int>();
                Dictionary<string, bool> drawSeen = new Dictionary<string, bool>();
                AddPreviewTemplates(drawTemplates, drawTypes, drawSeen, _hugGalleryTemplates, _hugGalleryTypes, PreviewTemplateDrawLimit);
                AddPreviewTemplates(drawTemplates, drawTypes, drawSeen, templates, typeIndices, PreviewTemplateDrawLimit);
                if (drawTemplates.Count > PreviewTemplateDrawLimit)
                {
                    drawTemplates.RemoveRange(PreviewTemplateDrawLimit, drawTemplates.Count - PreviewTemplateDrawLimit);
                    drawTypes.RemoveRange(PreviewTemplateDrawLimit, drawTypes.Count - PreviewTemplateDrawLimit);
                }
                if (drawTemplates.Count == 0)
                {
                    return;
                }
                string previewReason = reason;
                if (_hugGalleryTemplates != null && _hugGalleryTemplates.Count > 0)
                {
                    previewReason = "hug gallery " + _hugGalleryTemplates.Count.ToString(CultureInfo.InvariantCulture) + "  " + (reason ?? string.Empty);
                }
                _previewUpdated(new V4Preview
                {
                    Reason = previewReason,
                    TypeIndex = drawTypes.Count > 0 ? drawTypes[0] : 0,
                    Template = drawTemplates[0],
                    Templates = drawTemplates,
                    TypeIndices = drawTypes,
                    Input = _previewInput ?? input
                });
            }
            catch
            {
            }
        }

        private static void AddPreviewTemplates(List<V4Template> drawTemplates, List<int> drawTypes, Dictionary<string, bool> drawSeen, List<V4Template> templates, List<int> typeIndices, int limit)
        {
            if (templates == null)
            {
                return;
            }
            for (int i = 0; i < templates.Count && drawTemplates.Count < limit; i++)
            {
                V4Template template = templates[i];
                if (template == null)
                {
                    continue;
                }
                int typeIndex = 0;
                if (typeIndices != null && i < typeIndices.Count)
                {
                    typeIndex = typeIndices[i];
                }
                string key = typeIndex.ToString(CultureInfo.InvariantCulture) + ":" + TemplateKey(template);
                if (drawSeen.ContainsKey(key))
                {
                    continue;
                }
                drawSeen[key] = true;
                drawTemplates.Add(template);
                drawTypes.Add(typeIndex);
            }
        }

        private static void PumpUi(bool force)
        {
            Func<bool> cancelRequested = _cancelRequested;
            if (cancelRequested != null && cancelRequested())
            {
                throw new OperationCanceledException("V4 group nesting canceled by Esc.");
            }
            if ((GetAsyncKeyState(0x1B) & 0x8000) != 0)
            {
                throw new OperationCanceledException("V4 group nesting canceled by Esc.");
            }

            DateTime now = DateTime.UtcNow;
            if (!force && now < _nextUiPump)
            {
                return;
            }

            _nextUiPump = now.AddMilliseconds(250);
            if (System.Threading.Thread.CurrentThread.GetApartmentState() != System.Threading.ApartmentState.STA)
            {
                return;
            }
            try
            {
                WinForms.Application.DoEvents();
            }
            catch
            {
            }
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);
    }

    internal sealed class V4Template
    {
        public int Count;
        public List<ShapePolygon> Polygons = new List<ShapePolygon>();
        public ShapeBounds Bounds;
        public int PairRotation = -1;
        public bool ShelfLayout;
    }

    internal sealed class V4LayoutCell
    {
        public int X;
        public int Y;
        public char Orientation;
    }

    internal sealed class V4Strategy
    {
        public string Name;
        public int MainRotation;
        public int GroupSize;
        public double TemplateAreaWeight;
        public int ExpansionMode;
        public int CountMode;
        public int ShapeMode;
        public int RandomSalt;
        public double RandomWeight;
        public int ChoiceSpan;
    }

    internal sealed class V4PlacementChoice
    {
        public ShapePlacement Placement;
        public double Score;
    }

    internal sealed class V4ScoredPoint
    {
        public ShapePoint Point;
        public double Score;
    }

    internal sealed class V4FreeRegion
    {
        public ShapeBounds Bounds;
        public double Area;
        public List<ShapePoint> CornerPoints = new List<ShapePoint>();
    }

    internal sealed class V4Hole
    {
        public double MinX;
        public double MinY;
        public double MaxX;
        public double MaxY;
        public bool Internal;
        public double SortScore;
    }

    internal sealed class V4Timing
    {
        public long TemplateTicks;
        public int TemplateCalls;
        public long CandidateTicks;
        public int CandidateCalls;
        public int CandidatePoints;
        public long NfpTicks;
        public int NfpCalls;
        public long SettleTicks;
        public int SettleCalls;
        public long CollisionTicks;
        public int CollisionCalls;
        public long FreeRegionTicks;
        public int FreeRegionCalls;
        public int FreeRegionsFound;
        public long HoleTicks;
        public int HoleCalls;
        public int HolesFound;

        public static long Start()
        {
            return Stopwatch.GetTimestamp();
        }

        public static long Elapsed(long start)
        {
            return Stopwatch.GetTimestamp() - start;
        }

        public static string Format(long ticks)
        {
            double seconds = ticks / (double)Stopwatch.Frequency;
            if (seconds >= 10.0)
            {
                return seconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
            }
            return seconds.ToString("0.000", CultureInfo.InvariantCulture) + "s";
        }
    }

    internal sealed class V4Preview
    {
        public string Reason;
        public int TypeIndex;
        public V4Template Template;
        public List<V4Template> Templates;
        public List<int> TypeIndices;
        public ShapeNestingInput Input;
    }

    internal sealed class V4AsyncRun
    {
        private readonly object _lock = new object();
        private readonly List<string> _progress = new List<string>();
        private readonly List<ShapeNestingResult> _candidateQueue = new List<ShapeNestingResult>();
        private V4Preview _latestPreview;
        private bool _finished;
        private bool _drainPosted;
        private bool _cancelRequested;

        public readonly Document Document;
        public readonly ShapeNestingInput Input;
        public readonly Point3d Origin;
        public WinForms.Timer Timer;
        public WinForms.Control Invoker;
        public WinForms.MethodInvoker DrainCallback;
        public EventHandler IdleHandler;
        public ShapeNestingResult Result;
        public System.Exception Error;
        public V4RunLogger Logger;

        public V4AsyncRun(Document document, ShapeNestingInput input, Point3d origin)
        {
            Document = document;
            Input = input;
            Origin = origin;
        }

        public bool IsFinished
        {
            get
            {
                lock (_lock)
                {
                    return _finished;
                }
            }
        }

        public bool CancelRequested
        {
            get
            {
                lock (_lock)
                {
                    return _cancelRequested;
                }
            }
        }

        public bool RequestCancel()
        {
            lock (_lock)
            {
                if (_finished || _cancelRequested)
                {
                    return false;
                }
                _cancelRequested = true;
            }
            RequestDrain();
            return true;
        }

        public void EnqueueProgress(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }
            lock (_lock)
            {
                _progress.Add(message);
                if (_progress.Count > 80)
                {
                    _progress.RemoveRange(0, _progress.Count - 80);
                }
            }
            RequestDrain();
        }

        public List<string> TakeProgress()
        {
            lock (_lock)
            {
                List<string> copy = new List<string>(_progress);
                _progress.Clear();
                return copy;
            }
        }

        public void EnqueuePreview(V4Preview preview)
        {
            if (preview == null)
            {
                return;
            }
            lock (_lock)
            {
                _latestPreview = preview;
            }
            RequestDrain();
        }

        public V4Preview TakePreview()
        {
            lock (_lock)
            {
                V4Preview preview = _latestPreview;
                _latestPreview = null;
                return preview;
            }
        }

        public void EnqueueCandidate(ShapeNestingResult result)
        {
            if (result == null)
            {
                return;
            }
            lock (_lock)
            {
                _candidateQueue.Add(result);
                if (_candidateQueue.Count > 120)
                {
                    _candidateQueue.RemoveRange(0, _candidateQueue.Count - 120);
                }
            }
            RequestDrain();
        }

        public List<ShapeNestingResult> TakeCandidates()
        {
            lock (_lock)
            {
                List<ShapeNestingResult> results = new List<ShapeNestingResult>(_candidateQueue);
                _candidateQueue.Clear();
                return results;
            }
        }

        public void SetResult(ShapeNestingResult result)
        {
            lock (_lock)
            {
                Result = result;
                _finished = true;
            }
            RequestDrain();
        }

        public void SetError(System.Exception error)
        {
            lock (_lock)
            {
                Error = error;
                _finished = true;
            }
            RequestDrain();
        }

        public void RequestDrain()
        {
            WinForms.Control invoker = Invoker;
            WinForms.MethodInvoker callback = DrainCallback;
            if (invoker == null || callback == null || invoker.IsDisposed || !invoker.IsHandleCreated)
            {
                return;
            }

            lock (_lock)
            {
                if (_drainPosted)
                {
                    return;
                }
                _drainPosted = true;
            }

            try
            {
                invoker.BeginInvoke(new WinForms.MethodInvoker(delegate
                {
                    lock (_lock)
                    {
                        _drainPosted = false;
                    }
                    callback();
                }));
            }
            catch
            {
                lock (_lock)
                {
                    _drainPosted = false;
                }
            }
        }
    }

    internal sealed class V4RunLogger : IDisposable
    {
        private readonly object _lock = new object();
        private StreamWriter _writer;
        public readonly string FilePath;

        private V4RunLogger(string filePath, StreamWriter writer)
        {
            FilePath = filePath;
            _writer = writer;
        }

        public static V4RunLogger Start(Document doc, ShapeNestingInput input)
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cdxng4-logs");
            try
            {
                string dllDir = Path.GetDirectoryName(typeof(V4RunLogger).Assembly.Location);
                if (!string.IsNullOrEmpty(dllDir))
                {
                    root = Path.Combine(dllDir, "cdxng4-logs");
                }
            }
            catch
            {
            }

            Directory.CreateDirectory(root);
            string drawing = "drawing";
            try
            {
                if (doc != null && !string.IsNullOrEmpty(doc.Name))
                {
                    drawing = Path.GetFileNameWithoutExtension(doc.Name);
                }
            }
            catch
            {
            }
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                drawing = drawing.Replace(c, '_');
            }
            string filePath = Path.Combine(root, "cdxng4-" + drawing + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");
            StreamWriter writer = new StreamWriter(filePath, false);
            writer.AutoFlush = true;
            V4RunLogger logger = new V4RunLogger(filePath, writer);
            logger.Write("CDXNG4 log " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            logger.Write("\nVersion: " + CodexBridgeVersion.Version);
            if (doc != null)
            {
                try
                {
                    logger.Write("\nDocument: " + doc.Database.Filename);
                }
                catch
                {
                    logger.Write("\nDocument: " + doc.Name);
                }
            }
            if (input != null)
            {
                logger.Write("\nBoard: " + input.BoardWidth.ToString("0.###", CultureInfo.InvariantCulture) + " x " + input.BoardHeight.ToString("0.###", CultureInfo.InvariantCulture) + ", part types: " + input.PartTypes.Count.ToString(CultureInfo.InvariantCulture));
            }
            return logger;
        }

        public void Write(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }
            lock (_lock)
            {
                if (_writer == null)
                {
                    return;
                }
                _writer.Write(message);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_writer == null)
                {
                    return;
                }
                _writer.Flush();
                _writer.Dispose();
                _writer = null;
            }
        }
    }

    internal sealed class V4Score
    {
        public int BoardCount;
        public double AreaUtilization;
        public double LastRight;
        public double TotalRight;
        public double Envelope;
    }

    internal static class ShapeNestingV4Drawer
    {
        private const string ResultBoardLayer = "CDX_NEST_RESULT_BOARD";
        private const string ResultPartLayer = "CDX_NEST_RESULT_PART";
        private const string ResultTextLayer = "CDX_NEST_RESULT_TEXT";
        private const string PreviewBoardLayer = "CDX_NEST_PREVIEW_BOARD";
        private const string PreviewPartLayer = "CDX_NEST_PREVIEW_PART";
        private const string PreviewTextLayer = "CDX_NEST_PREVIEW_TEXT";
        private const int PreviewTemplateDrawLimit = 96;
        private const int SnapshotGenerationCount = 15;
        private const int SnapshotGenerationStride = 20;

        public static void RedrawFast(Document doc, ShapeNestingResult result, Point3d origin)
        {
            Database db = doc.Database;
            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                EnsureLayer(db, tr, ResultBoardLayer, 7);
                EnsureLayer(db, tr, ResultPartLayer, 3);
                EnsureLayer(db, tr, ResultTextLayer, 2);
                EnsureLayer(db, tr, PreviewBoardLayer, 4);
                EnsureLayer(db, tr, PreviewPartLayer, 1);
                EnsureLayer(db, tr, PreviewTextLayer, 6);

                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                ErasePreviousResult(ms, tr);
                double gap = 350.0;

                for (int b = 0; b < result.BoardCount; b++)
                {
                    double ox = origin.X;
                    double oy = origin.Y - b * (result.Input.BoardHeight + gap);
                    AppendPolygon(ms, tr, Rect(ox, oy, result.Input.BoardWidth, result.Input.BoardHeight), ResultBoardLayer, 7);
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
                }

                string source = result.Generation >= 0 && result.Individual > 0
                    ? " gen " + result.Generation.ToString(CultureInfo.InvariantCulture) + " individual " + result.Individual.ToString(CultureInfo.InvariantCulture)
                    : string.Empty;
                if (!string.IsNullOrEmpty(result.TraceLabel))
                {
                    source += "  " + result.TraceLabel;
                }
                string summary = "Shape nesting " + CodexBridgeVersion.Version + source + " sheets: " + result.BoardCount.ToString(CultureInfo.InvariantCulture) +
                    "  area utilization: " + (result.Utilization * 100.0).ToString("0.00", CultureInfo.InvariantCulture) + "%" +
                    "  last right remnant: " + result.MinRightRemnant.ToString("0.###", CultureInfo.InvariantCulture) +
                    "  total right: " + result.TotalRightRemnant.ToString("0.###", CultureInfo.InvariantCulture);
                AppendText(ms, tr, new Point3d(origin.X, origin.Y + result.Input.BoardHeight + 260, 0), 140, summary, ResultTextLayer, 2);
                tr.Commit();
            }
        }

        public static void DrawIndividualSnapshot(Document doc, ShapeNestingResult result, Point3d origin)
        {
            if (doc == null || result == null || result.Input == null)
            {
                return;
            }
            Point3d drawOrigin = IndividualSnapshotOrigin(result, origin);
            DrawResultAt(doc, result, drawOrigin, true, SnapshotLayerSuffix(result));
        }

        public static void DrawFinalSnapshot(Document doc, ShapeNestingResult result, Point3d origin)
        {
            if (doc == null || result == null || result.Input == null)
            {
                return;
            }
            Point3d drawOrigin = FinalSnapshotOrigin(result, origin);
            DrawResultAt(doc, result, drawOrigin, true, "_FINAL");
        }

        private static void DrawResultAt(Document doc, ShapeNestingResult result, Point3d origin, bool erasePrevious)
        {
            DrawResultAt(doc, result, origin, erasePrevious, string.Empty);
        }

        private static void DrawResultAt(Document doc, ShapeNestingResult result, Point3d origin, bool erasePrevious, string layerSuffix)
        {
            Database db = doc.Database;
            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                string boardLayer = ResultBoardLayer + layerSuffix;
                string partLayer = ResultPartLayer + layerSuffix;
                string textLayer = ResultTextLayer + layerSuffix;
                EnsureLayer(db, tr, boardLayer, 7);
                EnsureLayer(db, tr, partLayer, 3);
                EnsureLayer(db, tr, textLayer, 2);
                EnsureLayer(db, tr, PreviewBoardLayer, 4);
                EnsureLayer(db, tr, PreviewPartLayer, 1);
                EnsureLayer(db, tr, PreviewTextLayer, 6);

                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                if (erasePrevious)
                {
                    EraseLayer(ms, tr, boardLayer);
                    EraseLayer(ms, tr, partLayer);
                    EraseLayer(ms, tr, textLayer);
                    ErasePreviousPreview(ms, tr);
                }

                double gap = 350.0;
                for (int b = 0; b < result.BoardCount; b++)
                {
                    double ox = origin.X;
                    double oy = origin.Y - b * (result.Input.BoardHeight + gap);
                    AppendPolygon(ms, tr, Rect(ox, oy, result.Input.BoardWidth, result.Input.BoardHeight), boardLayer, 7);
                    AppendText(ms, tr, new Point3d(ox, oy + result.Input.BoardHeight + 80, 0), 120, SnapshotSheetLabel(result, b), textLayer, 2);
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
                    AppendPolygon(ms, tr, new ShapePolygon(moved), partLayer, p.Part.ColorIndex);
                }

                string summary = SnapshotSummary(result);
                AppendText(ms, tr, new Point3d(origin.X, origin.Y + result.Input.BoardHeight + 260, 0), 140, summary, textLayer, 2);
                tr.Commit();
            }
        }

        private static Point3d IndividualSnapshotOrigin(ShapeNestingResult result, Point3d origin)
        {
            int generation = Math.Max(0, result.Generation);
            int individual = Math.Max(1, result.Individual);
            int row = generation * SnapshotGenerationStride + individual - 1;
            double x = origin.X;
            double y = origin.Y - row * (result.Input.BoardHeight + 950.0);
            return new Point3d(x, y, origin.Z);
        }

        private static Point3d FinalSnapshotOrigin(ShapeNestingResult result, Point3d origin)
        {
            return origin;
        }

        private static string SnapshotLayerSuffix(ShapeNestingResult result)
        {
            int generation = Math.Max(0, result == null ? 0 : result.Generation);
            int individual = Math.Max(1, result == null ? 1 : result.Individual);
            return "_G" + generation.ToString(CultureInfo.InvariantCulture) + "_I" + individual.ToString(CultureInfo.InvariantCulture);
        }

        private static string SnapshotSheetLabel(ShapeNestingResult result, int boardIndex)
        {
            string label = "Sheet " + (boardIndex + 1).ToString(CultureInfo.InvariantCulture);
            if (result.Generation >= 0 && result.Individual > 0)
            {
                label = "G" + result.Generation.ToString(CultureInfo.InvariantCulture) +
                    " I" + result.Individual.ToString(CultureInfo.InvariantCulture) +
                    " " + label;
            }
            return label;
        }

        private static string SnapshotSummary(ShapeNestingResult result)
        {
            string source = result.Generation >= 0 && result.Individual > 0
                ? " gen " + result.Generation.ToString(CultureInfo.InvariantCulture) + " individual " + result.Individual.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
            if (!string.IsNullOrEmpty(result.TraceLabel))
            {
                source += "  " + result.TraceLabel;
            }
            return "Shape nesting " + CodexBridgeVersion.Version + source + " sheets: " + result.BoardCount.ToString(CultureInfo.InvariantCulture) +
                "  area utilization: " + (result.Utilization * 100.0).ToString("0.00", CultureInfo.InvariantCulture) + "%" +
                "  last right remnant: " + result.MinRightRemnant.ToString("0.###", CultureInfo.InvariantCulture) +
                "  total right: " + result.TotalRightRemnant.ToString("0.###", CultureInfo.InvariantCulture);
        }

        public static void RedrawPreview(Document doc, V4Preview preview, Point3d origin, ShapeNestingInput input)
        {
            if (doc == null || preview == null)
            {
                return;
            }
            List<V4Template> templates = preview.Templates != null && preview.Templates.Count > 0 ? preview.Templates : new List<V4Template> { preview.Template };
            if (templates.Count == 0 || templates[0] == null)
            {
                return;
            }
            Database db = doc.Database;
            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                EnsureLayer(db, tr, PreviewBoardLayer, 4);
                EnsureLayer(db, tr, PreviewPartLayer, 1);
                EnsureLayer(db, tr, PreviewTextLayer, 6);

                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                ErasePreviousPreview(ms, tr);

                double ox = origin.X + Math.Max(input.BoardWidth + 420.0, 2200.0);
                double oy = origin.Y;
                AppendText(ms, tr, new Point3d(ox, oy + input.BoardHeight + 120, 0), 80, preview.Reason ?? string.Empty, PreviewTextLayer, 6);

                double panelWidth = Math.Max(input.BoardWidth, 2600.0);
                double x = ox;
                double y = oy;
                double rowHeight = 0.0;
                for (int i = 0; i < templates.Count && i < PreviewTemplateDrawLimit; i++)
                {
                    V4Template template = templates[i];
                    if (template == null)
                    {
                        continue;
                    }
                    if (x > ox + 1e-6 && x + template.Bounds.Width > ox + panelWidth)
                    {
                        x = ox;
                        y -= rowHeight + 180.0;
                        rowHeight = 0.0;
                    }
                    int typeIndex = preview.TypeIndex;
                    if (preview.TypeIndices != null && i < preview.TypeIndices.Count)
                    {
                        typeIndex = preview.TypeIndices[i];
                    }
                    if (template.Bounds.Width > 1e-6 && template.Bounds.Height > 1e-6)
                    {
                        AppendPolygon(ms, tr, Rect(x, y, template.Bounds.Width, template.Bounds.Height), PreviewBoardLayer, 4);
                    }
                    foreach (ShapePolygon polygon in template.Polygons)
                    {
                        AppendPolygon(ms, tr, polygon.Translate(x, y), PreviewPartLayer, (short)(typeIndex % 7 + 1));
                    }
                    AppendText(ms, tr, new Point3d(x, y + template.Bounds.Height + 35, 0), 55, "x" + template.Count.ToString(CultureInfo.InvariantCulture), PreviewTextLayer, 6);
                    x += template.Bounds.Width + 160.0;
                    rowHeight = Math.Max(rowHeight, template.Bounds.Height + 90.0);
                }
                tr.Commit();
            }
        }

        private static ShapePolygon Rect(double x, double y, double width, double height)
        {
            return new ShapePolygon(new List<ShapePoint>
            {
                new ShapePoint(x, y),
                new ShapePoint(x + width, y),
                new ShapePoint(x + width, y + height),
                new ShapePoint(x, y + height)
            });
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
            ms.AppendEntity(text);
            tr.AddNewlyCreatedDBObject(text, true);
        }

        private static int ErasePreviousResult(BlockTableRecord ms, Transaction tr)
        {
            List<ObjectId> erase = new List<ObjectId>();
            foreach (ObjectId id in ms)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                if (ent == null || !IsResultLayer(ent.Layer))
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

        private static int EraseLayer(BlockTableRecord ms, Transaction tr, string layerName)
        {
            List<ObjectId> erase = new List<ObjectId>();
            foreach (ObjectId id in ms)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                if (ent == null || !string.Equals(ent.Layer, layerName, StringComparison.OrdinalIgnoreCase))
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

        private static int ErasePreviousPreview(BlockTableRecord ms, Transaction tr)
        {
            List<ObjectId> erase = new List<ObjectId>();
            foreach (ObjectId id in ms)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                if (ent == null || !IsPreviewLayer(ent.Layer))
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

        private static bool IsResultLayer(string layer)
        {
            return string.Equals(layer, ResultBoardLayer, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layer, ResultPartLayer, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layer, ResultTextLayer, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layer, "SHAPE_NEST_BOARD", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layer, "SHAPE_NEST_PART", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layer, "SHAPE_NEST_TEXT", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPreviewLayer(string layer)
        {
            return string.Equals(layer, PreviewBoardLayer, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layer, PreviewPartLayer, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layer, PreviewTextLayer, StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureLayer(Database db, Transaction tr, string name, short colorIndex)
        {
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name))
            {
                return;
            }
            lt.UpgradeOpen();
            LayerTableRecord layer = new LayerTableRecord();
            layer.Name = name;
            layer.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, colorIndex);
            lt.Add(layer);
            tr.AddNewlyCreatedDBObject(layer, true);
        }
    }
}
