using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(CodexAutoCADBridge.CodexCommands))]

namespace CodexAutoCADBridge
{
    internal static class CodexBridgeVersion
    {
        public const string Version = "GroupV4.132";
    }

    public class CodexCommands : IExtensionApplication
    {
        public void Initialize()
        {
            WriteLine("\nCodexAutoCADBridge " + CodexBridgeVersion.Version + " loaded. Main command: CDXNG4. Use CDX_VERSION to verify loaded DLL.");
        }

        public void Terminate()
        {
        }

        [CommandMethod("CDX_HELLO")]
        public void Hello()
        {
            WriteLine("\nCodexAutoCADBridge is ready.");
        }

        [CommandMethod("CDX_VERSION")]
        public void Version()
        {
            WriteLine("\nCodexAutoCADBridge " + CodexBridgeVersion.Version);
            WriteLine("\nAssembly: " + typeof(CodexCommands).Assembly.Location);
        }

        [CommandMethod("CDX_DRAWJSON")]
        public void DrawJson()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            Editor editor = doc.Editor;
            PromptOpenFileOptions options = new PromptOpenFileOptions("\nSelect Codex drawing JSON");
            options.Filter = "JSON drawing spec (*.json)|*.json|All files (*.*)|*.*";
            PromptFileNameResult result = editor.GetFileNameForOpen(options);
            if (result.Status != PromptStatus.OK)
            {
                return;
            }

            try
            {
                DrawSpecFromFile(doc, result.StringResult);
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage("\nCDX_DRAWJSON failed: " + ex.Message);
            }
        }

        [CommandMethod("CDX_BOLT_M10X25")]
        public void DrawBoltM10X25()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            Editor editor = doc.Editor;
            PromptPointOptions pointOptions = new PromptPointOptions("\nSpecify insertion point for M10x25 bolt three-view");
            PromptPointResult pointResult = editor.GetPoint(pointOptions);
            if (pointResult.Status != PromptStatus.OK)
            {
                return;
            }

            PromptDoubleOptions scaleOptions = new PromptDoubleOptions("\nDrawing scale <100>");
            scaleOptions.AllowNegative = false;
            scaleOptions.AllowZero = false;
            scaleOptions.DefaultValue = 100.0;
            scaleOptions.UseDefaultValue = true;
            PromptDoubleResult scaleResult = editor.GetDouble(scaleOptions);
            if (scaleResult.Status != PromptStatus.OK)
            {
                return;
            }

            try
            {
                Dictionary<string, object> spec = BoltSpecFactory.CreateM10x25(pointResult.Value.X, pointResult.Value.Y, scaleResult.Value);
                DrawSpec(doc, spec);
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage("\nCDX_BOLT_M10X25 failed: " + ex.Message);
            }
        }

        [CommandMethod("CDX_DRAG_LINK")]
        public void DragLink()
        {
            RunDragLink();
        }

        [CommandMethod("CDXDL")]
        public void DragLinkAlias()
        {
            RunDragLink();
        }

        [CommandMethod("CDX_BLOCK_BROWSER")]
        public void BlockBrowser()
        {
            using (BlockBrowserForm form = new BlockBrowserForm())
            {
                Application.ShowModalDialog(form);
                if (form.DialogResult == WinForms.DialogResult.OK)
                {
                    InsertExternalBlock(form.SelectedDwgPath, form.SelectedBlockName);
                }
            }
        }

        [CommandMethod("CDXBB")]
        public void BlockBrowserAlias()
        {
            BlockBrowser();
        }

        [CommandMethod("CDX_NEST_RECT")]
        public void NestRectangles()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            Editor editor = doc.Editor;
            editor.WriteMessage("\nRunning genetic rectangle nesting for 6000 x 1500 boards...");

            NestingResult result;
            try
            {
                result = RectNestingSolver.SolveDefault();
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage("\nNesting failed: " + ex.Message);
                return;
            }

            editor.WriteMessage("\nBest result: " + result.BoardCount.ToString(CultureInfo.InvariantCulture) + " boards, utilization " + (result.Utilization * 100.0).ToString("0.00", CultureInfo.InvariantCulture) + "%.");

            PromptPointOptions pointOptions = new PromptPointOptions("\nSpecify drawing origin for nesting result");
            PromptPointResult pointResult = editor.GetPoint(pointOptions);
            if (pointResult.Status != PromptStatus.OK)
            {
                return;
            }

            try
            {
                NestingDrawer.Draw(doc, result, pointResult.Value);
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage("\nDraw nesting result failed: " + ex.Message);
            }
        }

        [CommandMethod("CDXNR")]
        public void NestRectanglesAlias()
        {
            NestRectangles();
        }

        [CommandMethod("CDX_NEST_SHAPE")]
        public void NestShapes()
        {
            ShapeNestingWorkflow.Run(Application.DocumentManager.MdiActiveDocument);
        }

        [CommandMethod("CDXNS")]
        public void NestShapesAlias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS34")]
        public void NestShapesV34Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS35")]
        public void NestShapesV35Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS36")]
        public void NestShapesV36Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS37")]
        public void NestShapesV37Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS38")]
        public void NestShapesV38Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS39")]
        public void NestShapesV39Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS40")]
        public void NestShapesV40Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS41")]
        public void NestShapesV41Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS42")]
        public void NestShapesV42Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS43")]
        public void NestShapesV43Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS44")]
        public void NestShapesV44Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS45")]
        public void NestShapesV45Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS46")]
        public void NestShapesV46Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS47")]
        public void NestShapesV47Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS48")]
        public void NestShapesV48Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS49")]
        public void NestShapesV49Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS50")]
        public void NestShapesV50Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS51")]
        public void NestShapesV51Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS52")]
        public void NestShapesV52Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS53")]
        public void NestShapesV53Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS54")]
        public void NestShapesV54Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS55")]
        public void NestShapesV55Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS56")]
        public void NestShapesV56Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS57")]
        public void NestShapesV57Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS58")]
        public void NestShapesV58Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS59")]
        public void NestShapesV59Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS60")]
        public void NestShapesV60Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS61")]
        public void NestShapesV61Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS62")]
        public void NestShapesV62Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS63")]
        public void NestShapesV63Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS64")]
        public void NestShapesV64Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS65")]
        public void NestShapesV65Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS66")]
        public void NestShapesV66Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS67")]
        public void NestShapesV67Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS68")]
        public void NestShapesV68Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS69")]
        public void NestShapesV69Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS70")]
        public void NestShapesV70Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS71")]
        public void NestShapesV71Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS72")]
        public void NestShapesV72Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDXNS73")]
        public void NestShapesV73Alias()
        {
            NestShapes();
        }

        [CommandMethod("CDX_NEST_GROUP_V2")]
        public void NestShapeGroupsV2()
        {
            ShapeNestingGroupWorkflow.Run(Application.DocumentManager.MdiActiveDocument);
        }

        [CommandMethod("CDXNG2")]
        public void NestShapeGroupsV2Alias()
        {
            NestShapeGroupsV2();
        }

        [CommandMethod("CDXNG24")]
        public void NestShapeGroupsV24Alias()
        {
            NestShapeGroupsV2();
        }

        [CommandMethod("CDXNG25")]
        public void NestShapeGroupsV25Alias()
        {
            NestShapeGroupsV2();
        }

        [CommandMethod("CDXNG26")]
        public void NestShapeGroupsV26Alias()
        {
            NestShapeGroupsV2();
        }

        [CommandMethod("CDXNG35")]
        public void NestShapeGroupsV35Alias()
        {
            NestShapeGroupsV2();
        }

        [CommandMethod("CDX_NEST_GROUP_V4")]
        public void NestShapeGroupsV4()
        {
            ShapeNestingV4Workflow.Run(Application.DocumentManager.MdiActiveDocument);
        }

        [CommandMethod("CDXNG4")]
        public void NestShapeGroupsV4Alias()
        {
            NestShapeGroupsV4();
        }

        [CommandMethod("CDX_NEST_GROUP_V3")]
        public void NestShapeGroupsV3()
        {
            ShapeNestingV3Workflow.Run(Application.DocumentManager.MdiActiveDocument);
        }

        [CommandMethod("CDXNG3")]
        public void NestShapeGroupsV3Alias()
        {
            NestShapeGroupsV3();
        }

        [CommandMethod("CDX_CLEAR_NEST")]
        public void ClearShapeNestingResult()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }
            try
            {
                int count = ShapeNestingDrawer.Clear(doc);
                doc.Editor.WriteMessage("\nCleared " + count.ToString(CultureInfo.InvariantCulture) + " shape nesting result entities.");
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage("\nCDX_CLEAR_NEST failed: " + ex.Message);
            }
        }

        [CommandMethod("CDXCNCLR")]
        public void ClearShapeNestingResultAlias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR34")]
        public void ClearShapeNestingResultV34Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR35")]
        public void ClearShapeNestingResultV35Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR36")]
        public void ClearShapeNestingResultV36Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR37")]
        public void ClearShapeNestingResultV37Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR38")]
        public void ClearShapeNestingResultV38Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR39")]
        public void ClearShapeNestingResultV39Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR40")]
        public void ClearShapeNestingResultV40Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR41")]
        public void ClearShapeNestingResultV41Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR42")]
        public void ClearShapeNestingResultV42Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR43")]
        public void ClearShapeNestingResultV43Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR44")]
        public void ClearShapeNestingResultV44Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR45")]
        public void ClearShapeNestingResultV45Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR46")]
        public void ClearShapeNestingResultV46Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR47")]
        public void ClearShapeNestingResultV47Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR48")]
        public void ClearShapeNestingResultV48Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR49")]
        public void ClearShapeNestingResultV49Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR50")]
        public void ClearShapeNestingResultV50Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR51")]
        public void ClearShapeNestingResultV51Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR52")]
        public void ClearShapeNestingResultV52Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR53")]
        public void ClearShapeNestingResultV53Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR54")]
        public void ClearShapeNestingResultV54Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR55")]
        public void ClearShapeNestingResultV55Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR56")]
        public void ClearShapeNestingResultV56Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR57")]
        public void ClearShapeNestingResultV57Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR58")]
        public void ClearShapeNestingResultV58Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR59")]
        public void ClearShapeNestingResultV59Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR60")]
        public void ClearShapeNestingResultV60Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR61")]
        public void ClearShapeNestingResultV61Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR62")]
        public void ClearShapeNestingResultV62Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR63")]
        public void ClearShapeNestingResultV63Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR64")]
        public void ClearShapeNestingResultV64Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR65")]
        public void ClearShapeNestingResultV65Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR66")]
        public void ClearShapeNestingResultV66Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR67")]
        public void ClearShapeNestingResultV67Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR68")]
        public void ClearShapeNestingResultV68Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR69")]
        public void ClearShapeNestingResultV69Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR70")]
        public void ClearShapeNestingResultV70Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR71")]
        public void ClearShapeNestingResultV71Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR72")]
        public void ClearShapeNestingResultV72Alias()
        {
            ClearShapeNestingResult();
        }

        [CommandMethod("CDXCNCLR73")]
        public void ClearShapeNestingResultV73Alias()
        {
            ClearShapeNestingResult();
        }

        private static void RunDragLink()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            Editor editor = doc.Editor;
            PromptPointOptions firstOptions = new PromptPointOptions("\nSpecify first hole center");
            PromptPointResult firstResult = editor.GetPoint(firstOptions);
            if (firstResult.Status != PromptStatus.OK)
            {
                return;
            }

            LinkDragJig jig = new LinkDragJig(firstResult.Value);
            PromptResult dragResult = editor.Drag(jig);
            if (dragResult.Status != PromptStatus.OK)
            {
                return;
            }

            try
            {
                AddLinkEntities(doc, jig.FirstPoint, jig.SecondPoint);
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage("\nCDX_DRAG_LINK failed: " + ex.Message);
            }
        }

        private static void InsertExternalBlock(string sourceDwgPath, string blockName)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            Editor editor = doc.Editor;
            if (string.IsNullOrEmpty(sourceDwgPath) || string.IsNullOrEmpty(blockName))
            {
                editor.WriteMessage("\nNo block was selected.");
                return;
            }

            PromptPointOptions pointOptions = new PromptPointOptions("\nSpecify insertion point for block " + blockName);
            PromptPointResult pointResult = editor.GetPoint(pointOptions);
            if (pointResult.Status != PromptStatus.OK)
            {
                return;
            }

            try
            {
                using (DocumentLock docLock = doc.LockDocument())
                {
                    ObjectId blockId = ImportBlockDefinition(doc.Database, sourceDwgPath, blockName);
                    using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                    {
                        BlockTable blockTable = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                        BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                        BlockReference reference = new BlockReference(pointResult.Value, blockId);
                        modelSpace.AppendEntity(reference);
                        tr.AddNewlyCreatedDBObject(reference, true);
                        tr.Commit();
                    }
                }

                editor.Regen();
                editor.WriteMessage("\nInserted block " + blockName + ".");
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage("\nInsert block failed: " + ex.Message);
            }
        }

        private static ObjectId ImportBlockDefinition(Database targetDb, string sourceDwgPath, string blockName)
        {
            using (Transaction targetTr = targetDb.TransactionManager.StartTransaction())
            {
                BlockTable targetBlockTable = (BlockTable)targetTr.GetObject(targetDb.BlockTableId, OpenMode.ForRead);
                if (targetBlockTable.Has(blockName))
                {
                    ObjectId existing = targetBlockTable[blockName];
                    targetTr.Commit();
                    return existing;
                }
                targetTr.Commit();
            }

            using (Database sourceDb = new Database(false, true))
            {
                sourceDb.ReadDwgFile(sourceDwgPath, FileOpenMode.OpenForReadAndAllShare, true, null);
                sourceDb.CloseInput(true);

                ObjectId sourceBlockId;
                using (Transaction sourceTr = sourceDb.TransactionManager.StartTransaction())
                {
                    BlockTable sourceBlockTable = (BlockTable)sourceTr.GetObject(sourceDb.BlockTableId, OpenMode.ForRead);
                    if (!sourceBlockTable.Has(blockName))
                    {
                        throw new InvalidOperationException("Source DWG does not contain block: " + blockName);
                    }
                    sourceBlockId = sourceBlockTable[blockName];
                    sourceTr.Commit();
                }

                ObjectIdCollection ids = new ObjectIdCollection();
                ids.Add(sourceBlockId);
                IdMapping mapping = new IdMapping();
                sourceDb.WblockCloneObjects(ids, targetDb.BlockTableId, mapping, DuplicateRecordCloning.Ignore, false);

                using (Transaction targetTr = targetDb.TransactionManager.StartTransaction())
                {
                    BlockTable targetBlockTable = (BlockTable)targetTr.GetObject(targetDb.BlockTableId, OpenMode.ForRead);
                    if (!targetBlockTable.Has(blockName))
                    {
                        throw new InvalidOperationException("Block clone completed, but target drawing still does not contain: " + blockName);
                    }

                    ObjectId imported = targetBlockTable[blockName];
                    targetTr.Commit();
                    return imported;
                }
            }
        }

        private static void AddLinkEntities(Document doc, Point3d firstPoint, Point3d secondPoint)
        {
            Database db = doc.Database;
            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                EnsureLayer(db, tr, "CDX_LINK_VISIBLE", 2);
                EnsureLayer(db, tr, "CDX_LINK_HOLE", 2);
                EnsureLayer(db, tr, "CDX_LINK_CENTER", 1);
                EnsureLayer(db, tr, "CDX_LINK_TEXT", 3);

                BlockTable blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                List<Entity> entities = LinkShapeBuilder.Build(firstPoint, secondPoint, true);
                foreach (Entity entity in entities)
                {
                    modelSpace.AppendEntity(entity);
                    tr.AddNewlyCreatedDBObject(entity, true);
                }

                tr.Commit();
            }

            doc.Editor.Regen();
            doc.Editor.WriteMessage("\nCDX_DRAG_LINK completed.");
        }

        private static void DrawSpecFromFile(Document doc, string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("JSON file not found.", path);
            }

            string json = File.ReadAllText(path);
            object parsed = MiniJson.Parse(json);
            Dictionary<string, object> spec = RequireObject(parsed, "root");
            DrawSpec(doc, spec);
        }

        private static void DrawSpec(Document doc, Dictionary<string, object> spec)
        {
            Database db = doc.Database;
            int created = 0;

            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CreateLayers(db, tr, GetArray(spec, "layers", false));

                BlockTable blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                foreach (object item in GetArray(spec, "entities", true))
                {
                    Dictionary<string, object> entity = RequireObject(item, "entity");
                    string type = GetString(entity, "type", true).ToLowerInvariant();
                    string layer = GetString(entity, "layer", false);
                    if (string.IsNullOrEmpty(layer))
                    {
                        layer = "0";
                    }

                    if (type == "line")
                    {
                        Line line = new Line(
                            Point(entity, "x1", "y1"),
                            Point(entity, "x2", "y2"));
                        Append(modelSpace, tr, line, layer);
                        created++;
                    }
                    else if (type == "rectangle")
                    {
                        double x = GetDouble(entity, "x", true);
                        double y = GetDouble(entity, "y", true);
                        double width = GetDouble(entity, "width", true);
                        double height = GetDouble(entity, "height", true);
                        Point2dCollection points = new Point2dCollection();
                        points.Add(new Point2d(x, y));
                        points.Add(new Point2d(x + width, y));
                        points.Add(new Point2d(x + width, y + height));
                        points.Add(new Point2d(x, y + height));
                        Polyline polyline = PolylineFromPoints(points, true);
                        Append(modelSpace, tr, polyline, layer);
                        created++;
                    }
                    else if (type == "polyline")
                    {
                        Point2dCollection points = Points(entity);
                        bool closed = GetBool(entity, "closed", false, false);
                        Polyline polyline = PolylineFromPoints(points, closed);
                        Append(modelSpace, tr, polyline, layer);
                        created++;
                    }
                    else if (type == "circle")
                    {
                        Circle circle = new Circle(
                            Point(entity, "x", "y"),
                            Vector3d.ZAxis,
                            GetDouble(entity, "radius", true));
                        Append(modelSpace, tr, circle, layer);
                        created++;
                    }
                    else if (type == "arc")
                    {
                        Arc arc = new Arc(
                            Point(entity, "x", "y"),
                            GetDouble(entity, "radius", true),
                            DegreesToRadians(GetDouble(entity, "start_angle", true)),
                            DegreesToRadians(GetDouble(entity, "end_angle", true)));
                        Append(modelSpace, tr, arc, layer);
                        created++;
                    }
                    else if (type == "text")
                    {
                        DBText text = new DBText();
                        text.Position = Point(entity, "x", "y");
                        text.Height = GetDouble(entity, "height", false, 250.0);
                        text.TextString = GetString(entity, "text", true);
                        text.Rotation = DegreesToRadians(GetDouble(entity, "rotation", false, 0.0));
                        Append(modelSpace, tr, text, layer);
                        created++;
                    }
                    else
                    {
                        throw new InvalidOperationException("Unsupported entity type: " + type);
                    }
                }

                tr.Commit();
            }

            doc.Editor.Regen();
            doc.Editor.WriteMessage("\nCDX_DRAWJSON drew " + created.ToString(CultureInfo.InvariantCulture) + " entities.");
        }

        private static void CreateLayers(Database db, Transaction tr, List<object> layers)
        {
            if (layers == null)
            {
                return;
            }

            LayerTable layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (object item in layers)
            {
                Dictionary<string, object> layer = RequireObject(item, "layer");
                string name = GetString(layer, "name", true);
                if (string.IsNullOrWhiteSpace(name) || name == "0")
                {
                    continue;
                }

                if (!layerTable.Has(name))
                {
                    layerTable.UpgradeOpen();
                    LayerTableRecord record = new LayerTableRecord();
                    record.Name = name;
                    int color = (int)GetDouble(layer, "color", false, 7.0);
                    record.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)color);
                    layerTable.Add(record);
                    tr.AddNewlyCreatedDBObject(record, true);
                }
            }
        }

        private static void EnsureLayer(Database db, Transaction tr, string name, short colorIndex)
        {
            LayerTable layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (layerTable.Has(name))
            {
                return;
            }

            layerTable.UpgradeOpen();
            LayerTableRecord record = new LayerTableRecord();
            record.Name = name;
            record.Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex);
            layerTable.Add(record);
            tr.AddNewlyCreatedDBObject(record, true);
        }

        private static void Append(BlockTableRecord modelSpace, Transaction tr, Entity entity, string layer)
        {
            if (!string.IsNullOrEmpty(layer))
            {
                entity.Layer = layer;
            }

            modelSpace.AppendEntity(entity);
            tr.AddNewlyCreatedDBObject(entity, true);
        }

        private static Polyline PolylineFromPoints(Point2dCollection points, bool closed)
        {
            Polyline polyline = new Polyline(points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                polyline.AddVertexAt(i, points[i], 0.0, 0.0, 0.0);
            }

            polyline.Closed = closed;
            return polyline;
        }

        private static Point3d Point(Dictionary<string, object> obj, string xKey, string yKey)
        {
            return new Point3d(GetDouble(obj, xKey, true), GetDouble(obj, yKey, true), 0.0);
        }

        private static Point2dCollection Points(Dictionary<string, object> obj)
        {
            List<object> array = GetArray(obj, "points", true);
            Point2dCollection points = new Point2dCollection();
            foreach (object item in array)
            {
                List<object> pair = item as List<object>;
                if (pair == null || pair.Count < 2)
                {
                    throw new InvalidOperationException("Polyline points must be [x, y] arrays.");
                }

                points.Add(new Point2d(ToDouble(pair[0]), ToDouble(pair[1])));
            }

            return points;
        }

        private static Dictionary<string, object> RequireObject(object value, string name)
        {
            Dictionary<string, object> obj = value as Dictionary<string, object>;
            if (obj == null)
            {
                throw new InvalidOperationException(name + " must be a JSON object.");
            }

            return obj;
        }

        private static List<object> GetArray(Dictionary<string, object> obj, string key, bool required)
        {
            object value;
            if (!obj.TryGetValue(key, out value) || value == null)
            {
                if (required)
                {
                    throw new InvalidOperationException("Missing array property: " + key);
                }

                return null;
            }

            List<object> array = value as List<object>;
            if (array == null)
            {
                throw new InvalidOperationException(key + " must be an array.");
            }

            return array;
        }

        private static string GetString(Dictionary<string, object> obj, string key, bool required)
        {
            object value;
            if (!obj.TryGetValue(key, out value) || value == null)
            {
                if (required)
                {
                    throw new InvalidOperationException("Missing string property: " + key);
                }

                return null;
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static double GetDouble(Dictionary<string, object> obj, string key, bool required)
        {
            return GetDouble(obj, key, required, 0.0);
        }

        private static double GetDouble(Dictionary<string, object> obj, string key, bool required, double defaultValue)
        {
            object value;
            if (!obj.TryGetValue(key, out value) || value == null)
            {
                if (required)
                {
                    throw new InvalidOperationException("Missing number property: " + key);
                }

                return defaultValue;
            }

            return ToDouble(value);
        }

        private static bool GetBool(Dictionary<string, object> obj, string key, bool required, bool defaultValue)
        {
            object value;
            if (!obj.TryGetValue(key, out value) || value == null)
            {
                if (required)
                {
                    throw new InvalidOperationException("Missing bool property: " + key);
                }

                return defaultValue;
            }

            return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }

        private static double ToDouble(object value)
        {
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }

        private static double DegreesToRadians(double degrees)
        {
            return degrees * Math.PI / 180.0;
        }

        private static void WriteLine(string message)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc != null)
            {
                doc.Editor.WriteMessage(message);
            }
        }
    }

    internal static class BoltSpecFactory
    {
        public static Dictionary<string, object> CreateM10x25(double bx, double by, double scale)
        {
            SpecBuilder b = new SpecBuilder();
            b.Layer("BOLT_VISIBLE", 7);
            b.Layer("BOLT_THREAD", 3);
            b.Layer("BOLT_CENTER", 1);
            b.Layer("BOLT_DIM", 2);
            b.Layer("BOLT_TEXT", 5);
            b.Layer("BOLT_HIDDEN", 8);

            double d = 10 * scale;
            double r = d / 2.0;
            double d1 = 8.5 * scale;
            double r1 = d1 / 2.0;
            double length = 25 * scale;
            double pitch = 1.5 * scale;
            double headHeight = 6.4 * scale;
            double acrossFlats = 17 * scale;
            double acrossCorners = 19.6 * scale;
            double chamfer = 1.5 * scale;

            b.Text("BOLT_TEXT", bx, by + 7600, 320, "M10x25 hex bolt three-view", 0);
            b.Text("BOLT_TEXT", bx, by + 7150, 220, "Geometry scaled, dimensions shown in mm", 0);
            AddSideView(b, bx + 800, by + 5650, acrossCorners / 2.0, "Top view", "e~19.6", length, pitch, headHeight, r, r1, chamfer);
            AddSideView(b, bx + 800, by + 2500, acrossFlats / 2.0, "Front view", "s=17", length, pitch, headHeight, r, r1, chamfer);

            double fx = bx + 800;
            double fy = by + 2500;
            b.Line("BOLT_DIM", fx, fy - 1300, fx + length, fy - 1300);
            b.Line("BOLT_DIM", fx, fy - 1550, fx, fy - 1050);
            b.Line("BOLT_DIM", fx + length, fy - 1550, fx + length, fy - 1050);
            b.Text("BOLT_DIM", fx + length / 2.0 - 230, fy - 1650, 220, "L=25", 0);
            b.Line("BOLT_DIM", fx + length + 650, fy - r, fx + length + 650, fy + r);
            b.Line("BOLT_DIM", fx + length + 450, fy - r, fx + length + 850, fy - r);
            b.Line("BOLT_DIM", fx + length + 450, fy + r, fx + length + 850, fy + r);
            b.Text("BOLT_DIM", fx + length + 850, fy - 120, 220, "M10", 0);

            AddThreadEndView(b, bx + 7300, by + 2500, r, r1);
            AddHeadEndView(b, bx + 7300, by + 5650, acrossFlats, r);
            return b.Build();
        }

        private static void AddSideView(SpecBuilder b, double x0, double y0, double headHalf, string title, string headLabel, double length, double pitch, double k, double r, double r1, double chamfer)
        {
            b.Text("BOLT_TEXT", x0 - 800, y0 + headHalf + 820, 220, title, 0);
            b.Polyline("BOLT_VISIBLE", new double[,] {
                {x0 - k, y0 - headHalf + 120},
                {x0 - k + 120, y0 - headHalf},
                {x0, y0 - headHalf},
                {x0, y0 + headHalf},
                {x0 - k + 120, y0 + headHalf},
                {x0 - k, y0 + headHalf - 120}
            }, true);
            b.Line("BOLT_VISIBLE", x0 - k + 120, y0 - headHalf, x0 - k + 120, y0 + headHalf);
            b.Line("BOLT_VISIBLE", x0, y0 - headHalf, x0, y0 + headHalf);
            b.Line("BOLT_VISIBLE", x0, y0 - r, x0 + length - chamfer, y0 - r);
            b.Line("BOLT_VISIBLE", x0, y0 + r, x0 + length - chamfer, y0 + r);
            b.Line("BOLT_VISIBLE", x0 + length - chamfer, y0 - r, x0 + length, y0 - r1);
            b.Line("BOLT_VISIBLE", x0 + length - chamfer, y0 + r, x0 + length, y0 + r1);
            b.Line("BOLT_VISIBLE", x0 + length, y0 - r1, x0 + length, y0 + r1);
            b.Line("BOLT_THREAD", x0 + 180, y0 - r1, x0 + length - 200, y0 - r1);
            b.Line("BOLT_THREAD", x0 + 180, y0 + r1, x0 + length - 200, y0 + r1);
            for (double xx = x0 + pitch; xx < x0 + length - 260; xx += pitch * 2)
            {
                b.Line("BOLT_THREAD", xx, y0 - r, xx, y0 + r);
            }
            b.Line("BOLT_CENTER", x0 - k - 300, y0, x0 + length + 350, y0);
            b.Text("BOLT_DIM", x0 - k - 900, y0 + headHalf + 260, 200, headLabel, 0);
        }

        private static void AddThreadEndView(SpecBuilder b, double x, double y, double r, double r1)
        {
            b.Text("BOLT_TEXT", x - 900, y + 1300, 220, "Thread end view", 0);
            b.Circle("BOLT_VISIBLE", x, y, r);
            b.Circle("BOLT_THREAD", x, y, r1);
            b.Line("BOLT_CENTER", x - 850, y, x + 850, y);
            b.Line("BOLT_CENTER", x, y - 850, x, y + 850);
            b.Text("BOLT_DIM", x - 620, y - 1250, 220, "M10 thread end", 0);
        }

        private static void AddHeadEndView(SpecBuilder b, double x, double y, double acrossFlats, double r)
        {
            b.Text("BOLT_TEXT", x - 900, y + 1450, 220, "Head end view", 0);
            double radius = acrossFlats / (2.0 * Math.Sin(Math.PI / 3.0));
            double[,] points = new double[6, 2];
            int index = 0;
            foreach (double angle in new double[] { 0, 60, 120, 180, 240, 300 })
            {
                double radians = angle * Math.PI / 180.0;
                points[index, 0] = x + radius * Math.Cos(radians);
                points[index, 1] = y + radius * Math.Sin(radians);
                index++;
            }
            b.Polyline("BOLT_VISIBLE", points, true);
            b.Circle("BOLT_HIDDEN", x, y, r);
            b.Line("BOLT_CENTER", x - 1200, y, x + 1200, y);
            b.Line("BOLT_CENTER", x, y - 1200, x, y + 1200);
            b.Text("BOLT_DIM", x - 450, y - 1400, 220, "s=17", 0);
        }
    }

    internal sealed class LinkDragJig : DrawJig
    {
        private readonly Point3d _firstPoint;
        private Point3d _secondPoint;

        public LinkDragJig(Point3d firstPoint)
        {
            _firstPoint = firstPoint;
            _secondPoint = firstPoint + Vector3d.XAxis;
        }

        public Point3d FirstPoint
        {
            get { return _firstPoint; }
        }

        public Point3d SecondPoint
        {
            get { return _secondPoint; }
        }

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            JigPromptPointOptions options = new JigPromptPointOptions("\nSpecify second hole center");
            options.BasePoint = _firstPoint;
            options.UseBasePoint = true;
            options.Cursor = CursorType.RubberBand;
            options.UserInputControls =
                UserInputControls.Accept3dCoordinates |
                UserInputControls.GovernedByOrthoMode |
                UserInputControls.GovernedByUCSDetect |
                UserInputControls.UseBasePointElevation;

            PromptPointResult result = prompts.AcquirePoint(options);
            if (result.Status != PromptStatus.OK)
            {
                return SamplerStatus.Cancel;
            }

            if (result.Value.DistanceTo(_secondPoint) < Tolerance.Global.EqualPoint)
            {
                return SamplerStatus.NoChange;
            }

            _secondPoint = result.Value;
            return SamplerStatus.OK;
        }

        protected override bool WorldDraw(Autodesk.AutoCAD.GraphicsInterface.WorldDraw draw)
        {
            List<Entity> entities = LinkShapeBuilder.Build(_firstPoint, _secondPoint, false);
            foreach (Entity entity in entities)
            {
                using (entity)
                {
                    draw.Geometry.Draw(entity);
                }
            }

            return true;
        }
    }

    internal sealed class BlockBrowserForm : WinForms.Form
    {
        private const int GridColumns = 5;
        private const int GridRows = 3;
        private const int CardsPerPage = 15;
        private const string DefaultFolder = @"E:\以前的文件\彭辉\2019\OTL";

        private readonly WinForms.ListBox _fileList = new WinForms.ListBox();
        private readonly WinForms.TableLayoutPanel _blockGrid = new WinForms.TableLayoutPanel();
        private readonly WinForms.Label _pathLabel = new WinForms.Label();
        private readonly WinForms.Label _statusLabel = new WinForms.Label();
        private readonly WinForms.Label _pageLabel = new WinForms.Label();
        private readonly WinForms.Button _folderButton = new WinForms.Button();
        private readonly WinForms.Button _prevButton = new WinForms.Button();
        private readonly WinForms.Button _nextButton = new WinForms.Button();
        private readonly WinForms.Button _insertButton = new WinForms.Button();
        private readonly WinForms.TextBox _filterTextBox = new WinForms.TextBox();

        private readonly List<BlockPreviewInfo> _allBlocks = new List<BlockPreviewInfo>();
        private readonly List<BlockPreviewInfo> _filteredBlocks = new List<BlockPreviewInfo>();
        private readonly List<WinForms.Panel> _cards = new List<WinForms.Panel>();
        private int _pageIndex;
        private string _currentFolder;
        private string _currentDwgName;
        private BlockPreviewInfo _selectedBlock;

        public string SelectedDwgPath
        {
            get
            {
                return _selectedBlock == null ? null : _selectedBlock.SourceDwgPath;
            }
        }

        public string SelectedBlockName
        {
            get
            {
                return _selectedBlock == null ? null : _selectedBlock.Name;
            }
        }

        public BlockBrowserForm()
        {
            Text = "Codex Block Browser";
            StartPosition = WinForms.FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = true;
            Width = 1180;
            Height = 760;
            Font = new Drawing.Font("Microsoft YaHei UI", 9F, Drawing.FontStyle.Regular, Drawing.GraphicsUnit.Point);

            BuildLayout();
            Load += delegate { LoadInitialFolder(); };
        }

        private void BuildLayout()
        {
            WinForms.TableLayoutPanel root = new WinForms.TableLayoutPanel();
            root.Dock = WinForms.DockStyle.Fill;
            root.ColumnCount = 2;
            root.RowCount = 1;
            root.ColumnStyles.Add(new WinForms.ColumnStyle(WinForms.SizeType.Absolute, 320));
            root.ColumnStyles.Add(new WinForms.ColumnStyle(WinForms.SizeType.Percent, 100));
            Controls.Add(root);

            WinForms.Panel left = new WinForms.Panel();
            left.Dock = WinForms.DockStyle.Fill;
            left.Padding = new WinForms.Padding(10);
            root.Controls.Add(left, 0, 0);

            WinForms.Label leftTitle = new WinForms.Label();
            leftTitle.Text = "DWG 文件";
            leftTitle.Dock = WinForms.DockStyle.Top;
            leftTitle.Height = 26;
            leftTitle.Font = new Drawing.Font(Font, Drawing.FontStyle.Bold);
            left.Controls.Add(leftTitle);

            _folderButton.Text = "选择文件夹...";
            _folderButton.Dock = WinForms.DockStyle.Bottom;
            _folderButton.Height = 34;
            _folderButton.Click += delegate { ChooseFolder(); };
            left.Controls.Add(_folderButton);

            _pathLabel.Dock = WinForms.DockStyle.Bottom;
            _pathLabel.Height = 42;
            _pathLabel.AutoEllipsis = true;
            _pathLabel.TextAlign = Drawing.ContentAlignment.MiddleLeft;
            left.Controls.Add(_pathLabel);

            _fileList.Dock = WinForms.DockStyle.Fill;
            _fileList.IntegralHeight = false;
            _fileList.SelectedIndexChanged += delegate { LoadSelectedDwg(); };
            left.Controls.Add(_fileList);
            _fileList.BringToFront();

            WinForms.Panel right = new WinForms.Panel();
            right.Dock = WinForms.DockStyle.Fill;
            right.Padding = new WinForms.Padding(10);
            root.Controls.Add(right, 1, 0);

            WinForms.Panel top = new WinForms.Panel();
            top.Dock = WinForms.DockStyle.Top;
            top.Height = 46;
            right.Controls.Add(top);

            WinForms.Label blockTitle = new WinForms.Label();
            blockTitle.Text = "块预览";
            blockTitle.Dock = WinForms.DockStyle.Left;
            blockTitle.Width = 180;
            blockTitle.TextAlign = Drawing.ContentAlignment.MiddleLeft;
            blockTitle.Font = new Drawing.Font(Font, Drawing.FontStyle.Bold);
            top.Controls.Add(blockTitle);

            WinForms.Panel filterPanel = new WinForms.Panel();
            filterPanel.Dock = WinForms.DockStyle.Right;
            filterPanel.Width = 280;
            top.Controls.Add(filterPanel);

            WinForms.Label filterLabel = new WinForms.Label();
            filterLabel.Text = "过滤";
            filterLabel.Dock = WinForms.DockStyle.Left;
            filterLabel.Width = 44;
            filterLabel.TextAlign = Drawing.ContentAlignment.MiddleLeft;
            filterPanel.Controls.Add(filterLabel);

            _filterTextBox.Dock = WinForms.DockStyle.Fill;
            _filterTextBox.Margin = new WinForms.Padding(0);
            _filterTextBox.TextChanged += delegate { ApplyFilter(); };
            filterPanel.Controls.Add(_filterTextBox);
            _filterTextBox.BringToFront();

            _statusLabel.Dock = WinForms.DockStyle.Fill;
            _statusLabel.TextAlign = Drawing.ContentAlignment.MiddleLeft;
            top.Controls.Add(_statusLabel);

            WinForms.Panel pager = new WinForms.Panel();
            pager.Dock = WinForms.DockStyle.Bottom;
            pager.Height = 48;
            right.Controls.Add(pager);

            _prevButton.Text = "上一页";
            _prevButton.Width = 90;
            _prevButton.Height = 30;
            _prevButton.Left = 0;
            _prevButton.Top = 8;
            _prevButton.Click += delegate { ChangePage(-1); };
            pager.Controls.Add(_prevButton);

            _pageLabel.AutoSize = false;
            _pageLabel.Width = 160;
            _pageLabel.Height = 30;
            _pageLabel.Left = 100;
            _pageLabel.Top = 8;
            _pageLabel.TextAlign = Drawing.ContentAlignment.MiddleCenter;
            pager.Controls.Add(_pageLabel);

            _nextButton.Text = "下一页";
            _nextButton.Width = 90;
            _nextButton.Height = 30;
            _nextButton.Left = 270;
            _nextButton.Top = 8;
            _nextButton.Click += delegate { ChangePage(1); };
            pager.Controls.Add(_nextButton);

            _insertButton.Text = "插入";
            _insertButton.Width = 90;
            _insertButton.Height = 30;
            _insertButton.Left = 380;
            _insertButton.Top = 8;
            _insertButton.Enabled = false;
            _insertButton.Click += delegate { ConfirmInsert(); };
            pager.Controls.Add(_insertButton);

            _blockGrid.Dock = WinForms.DockStyle.Fill;
            _blockGrid.ColumnCount = GridColumns;
            _blockGrid.RowCount = GridRows;
            _blockGrid.Padding = new WinForms.Padding(0);
            _blockGrid.Margin = new WinForms.Padding(0);
            for (int col = 0; col < GridColumns; col++)
            {
                _blockGrid.ColumnStyles.Add(new WinForms.ColumnStyle(WinForms.SizeType.Percent, 100f / GridColumns));
            }
            for (int row = 0; row < GridRows; row++)
            {
                _blockGrid.RowStyles.Add(new WinForms.RowStyle(WinForms.SizeType.Percent, 100f / GridRows));
            }
            right.Controls.Add(_blockGrid);
            _blockGrid.BringToFront();
        }

        private void LoadInitialFolder()
        {
            if (Directory.Exists(DefaultFolder))
            {
                LoadFolder(DefaultFolder);
            }
            else
            {
                ChooseFolder();
            }
        }

        private void ChooseFolder()
        {
            using (WinForms.FolderBrowserDialog dialog = new WinForms.FolderBrowserDialog())
            {
                dialog.Description = "选择包含 DWG 文件的文件夹";
                dialog.SelectedPath = Directory.Exists(_currentFolder) ? _currentFolder : DefaultFolder;
                if (dialog.ShowDialog(this) == WinForms.DialogResult.OK)
                {
                    LoadFolder(dialog.SelectedPath);
                }
            }
        }

        private void LoadFolder(string folder)
        {
            _currentFolder = folder;
            _pathLabel.Text = folder;
            _fileList.Items.Clear();
            _allBlocks.Clear();
            _filteredBlocks.Clear();
            _selectedBlock = null;
            _insertButton.Enabled = false;
            _currentDwgName = null;
            _filterTextBox.Text = string.Empty;
            _pageIndex = 0;
            _blockGrid.Controls.Clear();

            string[] files = Directory.GetFiles(folder, "*.dwg");
            Array.Sort(files, StringComparer.CurrentCultureIgnoreCase);
            foreach (string file in files)
            {
                _fileList.Items.Add(new DwgFileItem(file));
            }

            _statusLabel.Text = "找到 " + files.Length.ToString(CultureInfo.InvariantCulture) + " 个 DWG 文件";
            UpdatePager();
            if (_fileList.Items.Count > 0)
            {
                _fileList.SelectedIndex = 0;
            }
        }

        private void LoadSelectedDwg()
        {
            DwgFileItem item = _fileList.SelectedItem as DwgFileItem;
            if (item == null)
            {
                return;
            }

            Cursor = WinForms.Cursors.WaitCursor;
            _statusLabel.Text = "正在读取 " + item.Name + " ...";
            _blockGrid.Controls.Clear();
            _allBlocks.Clear();
            _filteredBlocks.Clear();
            _selectedBlock = null;
            _insertButton.Enabled = false;
            _currentDwgName = item.Name;
            _pageIndex = 0;
            Update();

            try
            {
                List<BlockPreviewInfo> loaded = BlockPreviewReader.Read(item.Path);
                _allBlocks.AddRange(loaded);
                ApplyFilterCore(false);
            }
            catch (System.Exception ex)
            {
                _statusLabel.Text = "读取失败：" + ex.Message;
                _currentDwgName = null;
            }
            finally
            {
                Cursor = WinForms.Cursors.Default;
                RenderPage();
            }
        }

        private void ApplyFilter()
        {
            ApplyFilterCore(true);
            RenderPage();
        }

        private void ApplyFilterCore(bool resetPage)
        {
            if (resetPage)
            {
                _pageIndex = 0;
            }

            _filteredBlocks.Clear();
            string filter = (_filterTextBox.Text ?? string.Empty).Trim();
            if (filter.Length == 0)
            {
                _filteredBlocks.AddRange(_allBlocks);
            }
            else
            {
                foreach (BlockPreviewInfo block in _allBlocks)
                {
                    if (block.Name != null && block.Name.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0)
                    {
                        _filteredBlocks.Add(block);
                    }
                }
            }

            string fileName = string.IsNullOrEmpty(_currentDwgName) ? "DWG" : _currentDwgName;
            if (filter.Length == 0)
            {
                _statusLabel.Text = fileName + "：共 " + _allBlocks.Count.ToString(CultureInfo.InvariantCulture) + " 个块";
            }
            else
            {
                _statusLabel.Text = fileName + "：匹配 " + _filteredBlocks.Count.ToString(CultureInfo.InvariantCulture) + " / " + _allBlocks.Count.ToString(CultureInfo.InvariantCulture) + " 个块";
            }
        }

        private void ChangePage(int delta)
        {
            int pageCount = PageCount;
            if (pageCount <= 0)
            {
                return;
            }

            int next = _pageIndex + delta;
            if (next < 0)
            {
                next = 0;
            }
            if (next >= pageCount)
            {
                next = pageCount - 1;
            }
            if (next == _pageIndex)
            {
                return;
            }

            _pageIndex = next;
            RenderPage();
        }

        private int PageCount
        {
            get
            {
                if (_filteredBlocks.Count == 0)
                {
                    return 0;
                }
                return (_filteredBlocks.Count + CardsPerPage - 1) / CardsPerPage;
            }
        }

        private void RenderPage()
        {
            _blockGrid.SuspendLayout();
            _blockGrid.Controls.Clear();
            _cards.Clear();

            int start = _pageIndex * CardsPerPage;
            int end = Math.Min(start + CardsPerPage, _filteredBlocks.Count);
            for (int i = start; i < end; i++)
            {
                int pageOffset = i - start;
                int row = pageOffset / GridColumns;
                int col = pageOffset % GridColumns;
                _blockGrid.Controls.Add(CreateCard(_filteredBlocks[i]), col, row);
            }

            _blockGrid.ResumeLayout();
            UpdatePager();
        }

        private WinForms.Control CreateCard(BlockPreviewInfo info)
        {
            WinForms.Panel card = new WinForms.Panel();
            card.Dock = WinForms.DockStyle.Fill;
            card.Margin = new WinForms.Padding(8);
            card.BorderStyle = WinForms.BorderStyle.FixedSingle;
            card.Tag = info;

            WinForms.PictureBox picture = new WinForms.PictureBox();
            picture.Dock = WinForms.DockStyle.Fill;
            picture.Margin = new WinForms.Padding(8, 8, 8, 42);
            picture.SizeMode = WinForms.PictureBoxSizeMode.Zoom;
            picture.BackColor = Drawing.Color.Black;
            picture.Image = info.Image;
            card.Controls.Add(picture);

            WinForms.Label label = new WinForms.Label();
            label.Dock = WinForms.DockStyle.Bottom;
            label.Height = 34;
            label.TextAlign = Drawing.ContentAlignment.MiddleCenter;
            label.AutoEllipsis = true;
            label.Text = info.Name;
            card.Controls.Add(label);

            card.Click += delegate { SelectBlock(info); };
            picture.Click += delegate { SelectBlock(info); };
            label.Click += delegate { SelectBlock(info); };
            card.DoubleClick += delegate { SelectBlock(info); ConfirmInsert(); };
            picture.DoubleClick += delegate { SelectBlock(info); ConfirmInsert(); };
            label.DoubleClick += delegate { SelectBlock(info); ConfirmInsert(); };

            WinForms.ToolTip tip = new WinForms.ToolTip();
            tip.SetToolTip(card, info.Name);
            tip.SetToolTip(picture, info.Name);
            tip.SetToolTip(label, info.Name);

            _cards.Add(card);
            UpdateCardSelection(card, info);
            return card;
        }

        private void SelectBlock(BlockPreviewInfo info)
        {
            _selectedBlock = info;
            _insertButton.Enabled = _selectedBlock != null;
            foreach (WinForms.Panel card in _cards)
            {
                UpdateCardSelection(card, card.Tag as BlockPreviewInfo);
            }
        }

        private void UpdateCardSelection(WinForms.Panel card, BlockPreviewInfo info)
        {
            bool selected = _selectedBlock != null && info != null && string.Equals(_selectedBlock.Name, info.Name, StringComparison.Ordinal) && string.Equals(_selectedBlock.SourceDwgPath, info.SourceDwgPath, StringComparison.OrdinalIgnoreCase);
            card.BackColor = selected ? Drawing.Color.FromArgb(210, 230, 255) : Drawing.SystemColors.Control;
            card.Padding = selected ? new WinForms.Padding(2) : new WinForms.Padding(0);
        }

        private void ConfirmInsert()
        {
            if (_selectedBlock == null)
            {
                return;
            }

            DialogResult = WinForms.DialogResult.OK;
            Close();
        }

        private void UpdatePager()
        {
            int pageCount = PageCount;
            if (pageCount == 0)
            {
                _pageLabel.Text = "第 0 / 0 页";
                _prevButton.Enabled = false;
                _nextButton.Enabled = false;
            }
            else
            {
                _pageLabel.Text = "第 " + (_pageIndex + 1).ToString(CultureInfo.InvariantCulture) + " / " + pageCount.ToString(CultureInfo.InvariantCulture) + " 页";
                _prevButton.Enabled = _pageIndex > 0;
                _nextButton.Enabled = _pageIndex < pageCount - 1;
            }
        }
    }

    internal sealed class DwgFileItem
    {
        public readonly string Path;
        public readonly string Name;

        public DwgFileItem(string path)
        {
            Path = path;
            Name = System.IO.Path.GetFileName(path);
        }

        public override string ToString()
        {
            return Name;
        }
    }

    internal sealed class BlockPreviewInfo
    {
        public readonly string SourceDwgPath;
        public readonly string Name;
        public readonly Drawing.Bitmap Image;

        public BlockPreviewInfo(string sourceDwgPath, string name, Drawing.Bitmap image)
        {
            SourceDwgPath = sourceDwgPath;
            Name = name;
            Image = image;
        }
    }

    internal static class BlockPreviewReader
    {
        public static List<BlockPreviewInfo> Read(string dwgPath)
        {
            List<BlockPreviewInfo> blocks = new List<BlockPreviewInfo>();
            using (Database db = new Database(false, true))
            {
                db.ReadDwgFile(dwgPath, FileOpenMode.OpenForReadAndAllShare, true, null);
                db.CloseInput(true);

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTable blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    foreach (ObjectId id in blockTable)
                    {
                        BlockTableRecord record = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                        if (ShouldSkip(record))
                        {
                            continue;
                        }

                        Drawing.Bitmap bitmap = TryGetPreviewIcon(record);
                        if (bitmap == null)
                        {
                            bitmap = DrawVectorPreview(record, tr);
                        }
                        blocks.Add(new BlockPreviewInfo(dwgPath, record.Name, bitmap));
                    }

                    tr.Commit();
                }
            }

            blocks.Sort(delegate (BlockPreviewInfo a, BlockPreviewInfo b)
            {
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            return blocks;
        }

        private static bool ShouldSkip(BlockTableRecord record)
        {
            if (record.IsAnonymous || record.IsLayout)
            {
                return true;
            }

            string name = record.Name;
            return string.IsNullOrEmpty(name) || name.StartsWith("*", StringComparison.Ordinal);
        }

        private static Drawing.Bitmap TryGetPreviewIcon(BlockTableRecord record)
        {
            try
            {
                if (record.PreviewIcon != null)
                {
                    return new Drawing.Bitmap(record.PreviewIcon);
                }
            }
            catch
            {
            }
            return null;
        }

        private static Drawing.Bitmap DrawVectorPreview(BlockTableRecord record, Transaction tr)
        {
            List<PreviewSegment> segments = new List<PreviewSegment>();
            Extents2d extents = new Extents2d();
            bool hasExtents = false;

            foreach (ObjectId entityId in record)
            {
                Entity entity = tr.GetObject(entityId, OpenMode.ForRead, false) as Entity;
                if (entity == null)
                {
                    continue;
                }

                CollectEntity(entity, segments, ref extents, ref hasExtents);
            }

            if (!hasExtents || segments.Count == 0)
            {
                return Placeholder(record.Name);
            }

            return RenderSegments(record.Name, segments, extents);
        }

        private static void CollectEntity(Entity entity, List<PreviewSegment> segments, ref Extents2d extents, ref bool hasExtents)
        {
            Line line = entity as Line;
            if (line != null)
            {
                AddSegment(segments, ref extents, ref hasExtents, line.StartPoint, line.EndPoint);
                return;
            }

            Polyline polyline = entity as Polyline;
            if (polyline != null)
            {
                int count = polyline.NumberOfVertices;
                for (int i = 0; i < count - 1; i++)
                {
                    AddSegment(segments, ref extents, ref hasExtents, Point(polyline.GetPoint2dAt(i)), Point(polyline.GetPoint2dAt(i + 1)));
                }
                if (polyline.Closed && count > 1)
                {
                    AddSegment(segments, ref extents, ref hasExtents, Point(polyline.GetPoint2dAt(count - 1)), Point(polyline.GetPoint2dAt(0)));
                }
                return;
            }

            Circle circle = entity as Circle;
            if (circle != null)
            {
                AddCircle(segments, ref extents, ref hasExtents, circle.Center, circle.Radius, 40, 0.0, Math.PI * 2.0);
                return;
            }

            Arc arc = entity as Arc;
            if (arc != null)
            {
                AddCircle(segments, ref extents, ref hasExtents, arc.Center, arc.Radius, 32, arc.StartAngle, arc.EndAngle);
            }
        }

        private static void AddCircle(List<PreviewSegment> segments, ref Extents2d extents, ref bool hasExtents, Point3d center, double radius, int steps, double start, double end)
        {
            double sweep = end - start;
            if (sweep <= 0)
            {
                sweep += Math.PI * 2.0;
            }

            Point3d previous = PointOnCircle(center, radius, start);
            for (int i = 1; i <= steps; i++)
            {
                double angle = start + sweep * i / steps;
                Point3d current = PointOnCircle(center, radius, angle);
                AddSegment(segments, ref extents, ref hasExtents, previous, current);
                previous = current;
            }
        }

        private static Point3d PointOnCircle(Point3d center, double radius, double angle)
        {
            return new Point3d(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius, 0.0);
        }

        private static Point3d Point(Point2d point)
        {
            return new Point3d(point.X, point.Y, 0.0);
        }

        private static void AddSegment(List<PreviewSegment> segments, ref Extents2d extents, ref bool hasExtents, Point3d a, Point3d b)
        {
            segments.Add(new PreviewSegment(a.X, a.Y, b.X, b.Y));
            Include(ref extents, ref hasExtents, a.X, a.Y);
            Include(ref extents, ref hasExtents, b.X, b.Y);
        }

        private static void Include(ref Extents2d extents, ref bool hasExtents, double x, double y)
        {
            if (!hasExtents)
            {
                extents = new Extents2d(new Point2d(x, y), new Point2d(x, y));
                hasExtents = true;
            }
            else
            {
                extents = new Extents2d(
                    new Point2d(Math.Min(extents.MinPoint.X, x), Math.Min(extents.MinPoint.Y, y)),
                    new Point2d(Math.Max(extents.MaxPoint.X, x), Math.Max(extents.MaxPoint.Y, y)));
            }
        }

        private static Drawing.Bitmap RenderSegments(string name, List<PreviewSegment> segments, Extents2d extents)
        {
            int width = 132;
            int height = 120;
            Drawing.Bitmap bitmap = new Drawing.Bitmap(width, height);
            using (Drawing.Graphics g = Drawing.Graphics.FromImage(bitmap))
            using (Drawing.Pen pen = new Drawing.Pen(Drawing.Color.Yellow, 1.3f))
            {
                g.Clear(Drawing.Color.Black);
                g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;

                double minX = extents.MinPoint.X;
                double minY = extents.MinPoint.Y;
                double maxX = extents.MaxPoint.X;
                double maxY = extents.MaxPoint.Y;
                double spanX = Math.Max(maxX - minX, 1e-6);
                double spanY = Math.Max(maxY - minY, 1e-6);
                double scale = Math.Min((width - 18) / spanX, (height - 18) / spanY);
                double offsetX = (width - spanX * scale) / 2.0;
                double offsetY = (height - spanY * scale) / 2.0;

                foreach (PreviewSegment segment in segments)
                {
                    float x1 = (float)(offsetX + (segment.X1 - minX) * scale);
                    float y1 = (float)(height - (offsetY + (segment.Y1 - minY) * scale));
                    float x2 = (float)(offsetX + (segment.X2 - minX) * scale);
                    float y2 = (float)(height - (offsetY + (segment.Y2 - minY) * scale));
                    g.DrawLine(pen, x1, y1, x2, y2);
                }
            }
            return bitmap;
        }

        private static Drawing.Bitmap Placeholder(string name)
        {
            int width = 132;
            int height = 120;
            Drawing.Bitmap bitmap = new Drawing.Bitmap(width, height);
            using (Drawing.Graphics g = Drawing.Graphics.FromImage(bitmap))
            using (Drawing.Brush brush = new Drawing.SolidBrush(Drawing.Color.FromArgb(34, 34, 34)))
            using (Drawing.Pen pen = new Drawing.Pen(Drawing.Color.DimGray))
            using (Drawing.Brush text = new Drawing.SolidBrush(Drawing.Color.Gainsboro))
            using (Drawing.StringFormat format = new Drawing.StringFormat())
            {
                g.Clear(Drawing.Color.Black);
                g.FillRectangle(brush, 8, 8, width - 16, height - 16);
                g.DrawRectangle(pen, 8, 8, width - 17, height - 17);
                format.Alignment = Drawing.StringAlignment.Center;
                format.LineAlignment = Drawing.StringAlignment.Center;
                g.DrawString(name, new Drawing.Font("Microsoft YaHei UI", 8F), text, new Drawing.RectangleF(12, 12, width - 24, height - 24), format);
            }
            return bitmap;
        }
    }

    internal struct PreviewSegment
    {
        public readonly double X1;
        public readonly double Y1;
        public readonly double X2;
        public readonly double Y2;

        public PreviewSegment(double x1, double y1, double x2, double y2)
        {
            X1 = x1;
            Y1 = y1;
            X2 = x2;
            Y2 = y2;
        }
    }

    internal static class LinkShapeBuilder
    {
        public static List<Entity> Build(Point3d firstPoint, Point3d secondPoint, bool final)
        {
            List<Entity> entities = new List<Entity>();
            Vector3d axis = secondPoint - firstPoint;
            double length = axis.Length;
            if (length < 1e-6)
            {
                return entities;
            }

            Vector3d xAxis = axis.GetNormal();
            Vector3d yAxis = Vector3d.ZAxis.CrossProduct(xAxis).GetNormal();
            double gripRadius = Math.Max(length * 0.055, 180.0);
            double bodyHalfWidth = Math.Max(length * 0.025, 90.0);
            double neckHalfWidth = Math.Max(length * 0.035, 130.0);
            double holeRadius = Math.Max(gripRadius * 0.30, 55.0);
            double blendLength = Math.Min(Math.Max(length * 0.12, gripRadius * 1.6), length * 0.35);
            double neckLength = Math.Min(Math.Max(length * 0.08, gripRadius * 1.1), length * 0.25);

            Point3d p0 = firstPoint;
            Point3d p1 = secondPoint;
            Point3d a = p0 + xAxis * neckLength;
            Point3d b = p0 + xAxis * (neckLength + blendLength);
            Point3d c = p1 - xAxis * (neckLength + blendLength);
            Point3d d = p1 - xAxis * neckLength;

            Polyline outline = new Polyline(12);
            AddVertex(outline, 0, p0 - xAxis * gripRadius * 0.65 + yAxis * neckHalfWidth);
            AddVertex(outline, 1, a + yAxis * neckHalfWidth);
            AddVertex(outline, 2, b + yAxis * bodyHalfWidth);
            AddVertex(outline, 3, c + yAxis * bodyHalfWidth);
            AddVertex(outline, 4, d + yAxis * neckHalfWidth);
            AddVertex(outline, 5, p1 + xAxis * gripRadius * 0.65 + yAxis * neckHalfWidth);
            AddVertex(outline, 6, p1 + xAxis * gripRadius + yAxis * 0.0);
            AddVertex(outline, 7, p1 + xAxis * gripRadius * 0.65 - yAxis * neckHalfWidth);
            AddVertex(outline, 8, d - yAxis * neckHalfWidth);
            AddVertex(outline, 9, c - yAxis * bodyHalfWidth);
            AddVertex(outline, 10, b - yAxis * bodyHalfWidth);
            AddVertex(outline, 11, a - yAxis * neckHalfWidth);
            AddVertex(outline, 12, p0 - xAxis * gripRadius * 0.65 - yAxis * neckHalfWidth);
            AddVertex(outline, 13, p0 - xAxis * gripRadius + yAxis * 0.0);
            outline.Closed = true;
            SetEntityDisplay(outline, final, "CDX_LINK_VISIBLE", 2);
            entities.Add(outline);

            Circle hole0 = new Circle(p0, Vector3d.ZAxis, holeRadius);
            SetEntityDisplay(hole0, final, "CDX_LINK_HOLE", 2);
            entities.Add(hole0);

            Circle hole1 = new Circle(p1, Vector3d.ZAxis, holeRadius);
            SetEntityDisplay(hole1, final, "CDX_LINK_HOLE", 2);
            entities.Add(hole1);

            Line center = new Line(p0, p1);
            SetEntityDisplay(center, final, "CDX_LINK_CENTER", 1);
            entities.Add(center);

            DBText label = new DBText();
            label.TextString = Math.Round(length, 0).ToString(CultureInfo.InvariantCulture);
            label.Height = Math.Max(length * 0.045, 180.0);
            label.Position = MidPoint(p0, p1) + yAxis * (bodyHalfWidth * 1.8);
            label.Rotation = Math.Atan2(xAxis.Y, xAxis.X);
            label.HorizontalMode = TextHorizontalMode.TextCenter;
            label.VerticalMode = TextVerticalMode.TextVerticalMid;
            label.AlignmentPoint = label.Position;
            SetEntityDisplay(label, final, "CDX_LINK_TEXT", 3);
            entities.Add(label);

            return entities;
        }

        private static void SetEntityDisplay(Entity entity, bool final, string layer, short previewColor)
        {
            if (final)
            {
                entity.Layer = layer;
                entity.ColorIndex = 256;
            }
            else
            {
                entity.ColorIndex = previewColor;
            }
        }

        private static void AddVertex(Polyline polyline, int index, Point3d point)
        {
            polyline.AddVertexAt(index, new Point2d(point.X, point.Y), 0.0, 0.0, 0.0);
        }

        private static Point3d MidPoint(Point3d a, Point3d b)
        {
            return new Point3d((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0, (a.Z + b.Z) / 2.0);
        }
    }

    internal sealed class NestPartType
    {
        public readonly string Name;
        public readonly double Width;
        public readonly double Height;
        public readonly int Quantity;
        public readonly short ColorIndex;

        public NestPartType(string name, double width, double height, int quantity, short colorIndex)
        {
            Name = name;
            Width = width;
            Height = height;
            Quantity = quantity;
            ColorIndex = colorIndex;
        }
    }

    internal sealed class NestPartInstance
    {
        public readonly int TypeIndex;
        public readonly int Number;
        public readonly string Name;
        public readonly double Width;
        public readonly double Height;
        public readonly short ColorIndex;

        public NestPartInstance(int typeIndex, int number, string name, double width, double height, short colorIndex)
        {
            TypeIndex = typeIndex;
            Number = number;
            Name = name;
            Width = width;
            Height = height;
            ColorIndex = colorIndex;
        }
    }

    internal sealed class NestPlacement
    {
        public NestPartInstance Part;
        public int BoardIndex;
        public double X;
        public double Y;
        public double Width;
        public double Height;
        public bool Rotated;
    }

    internal sealed class NestFreeRect
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;

        public NestFreeRect(double x, double y, double width, double height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }
    }

    internal sealed class NestingResult
    {
        public readonly List<NestPlacement> Placements;
        public readonly int BoardCount;
        public readonly double BoardWidth;
        public readonly double BoardHeight;
        public readonly double TotalPartArea;
        public readonly double Utilization;

        public NestingResult(List<NestPlacement> placements, int boardCount, double boardWidth, double boardHeight, double totalPartArea)
        {
            Placements = placements;
            BoardCount = boardCount;
            BoardWidth = boardWidth;
            BoardHeight = boardHeight;
            TotalPartArea = totalPartArea;
            Utilization = totalPartArea / (boardCount * boardWidth * boardHeight);
        }
    }

    internal sealed class NestChromosome
    {
        public int[] Order;
        public bool[] Rotate;
        public NestingResult Result;
        public double Fitness;

        public NestChromosome Clone()
        {
            NestChromosome clone = new NestChromosome();
            clone.Order = (int[])Order.Clone();
            clone.Rotate = (bool[])Rotate.Clone();
            clone.Result = Result;
            clone.Fitness = Fitness;
            return clone;
        }
    }

    internal static class RectNestingSolver
    {
        private const double BoardWidth = 6000.0;
        private const double BoardHeight = 1500.0;
        private const int PopulationSize = 90;
        private const int Generations = 220;
        private const double MutationRate = 0.22;
        private const double RotationMutationRate = 0.06;

        public static NestingResult SolveDefault()
        {
            NestPartType[] types = new NestPartType[]
            {
                new NestPartType("P1", 330.593, 275.809, 30, 30),
                new NestPartType("P2", 235.613, 286.184, 50, 3),
                new NestPartType("P3", 74.923, 273.379, 25, 4),
                new NestPartType("P4", 305.664, 305.664, 60, 2),
                new NestPartType("P5", 291.538, 283.117, 45, 5)
            };

            List<NestPartInstance> parts = Expand(types);
            Random random = new Random(20260615);
            List<NestChromosome> population = CreateInitialPopulation(parts, random);
            NestChromosome best = null;

            for (int generation = 0; generation < Generations; generation++)
            {
                Evaluate(population, parts);
                population.Sort(CompareChromosomes);
                if (best == null || population[0].Fitness < best.Fitness)
                {
                    best = population[0].Clone();
                }

                List<NestChromosome> next = new List<NestChromosome>();
                int elite = Math.Min(8, population.Count);
                for (int i = 0; i < elite; i++)
                {
                    next.Add(population[i].Clone());
                }

                while (next.Count < PopulationSize)
                {
                    NestChromosome a = Tournament(population, random);
                    NestChromosome b = Tournament(population, random);
                    NestChromosome child = Crossover(a, b, random);
                    Mutate(child, random);
                    next.Add(child);
                }

                population = next;
            }

            Evaluate(population, parts);
            population.Sort(CompareChromosomes);
            if (best == null || population[0].Fitness < best.Fitness)
            {
                best = population[0].Clone();
            }

            return best.Result;
        }

        private static List<NestPartInstance> Expand(NestPartType[] types)
        {
            List<NestPartInstance> parts = new List<NestPartInstance>();
            for (int t = 0; t < types.Length; t++)
            {
                for (int i = 1; i <= types[t].Quantity; i++)
                {
                    parts.Add(new NestPartInstance(t, i, types[t].Name, types[t].Width, types[t].Height, types[t].ColorIndex));
                }
            }
            return parts;
        }

        private static List<NestChromosome> CreateInitialPopulation(List<NestPartInstance> parts, Random random)
        {
            List<NestChromosome> population = new List<NestChromosome>();
            int n = parts.Count;

            population.Add(OrderedChromosome(parts, delegate (NestPartInstance p) { return -(p.Width * p.Height); }, false));
            population.Add(OrderedChromosome(parts, delegate (NestPartInstance p) { return -Math.Max(p.Width, p.Height); }, true));
            population.Add(OrderedChromosome(parts, delegate (NestPartInstance p) { return -p.Height; }, false));
            population.Add(OrderedChromosome(parts, delegate (NestPartInstance p) { return -p.Width; }, true));

            while (population.Count < PopulationSize)
            {
                NestChromosome chromosome = new NestChromosome();
                chromosome.Order = new int[n];
                chromosome.Rotate = new bool[n];
                for (int i = 0; i < n; i++)
                {
                    chromosome.Order[i] = i;
                    chromosome.Rotate[i] = random.NextDouble() < 0.5;
                }
                Shuffle(chromosome.Order, random);
                population.Add(chromosome);
            }
            return population;
        }

        private static NestChromosome OrderedChromosome(List<NestPartInstance> parts, Converter<NestPartInstance, double> key, bool rotateLarge)
        {
            List<int> order = new List<int>();
            for (int i = 0; i < parts.Count; i++)
            {
                order.Add(i);
            }
            order.Sort(delegate (int a, int b)
            {
                int cmp = key(parts[a]).CompareTo(key(parts[b]));
                if (cmp != 0)
                {
                    return cmp;
                }
                return a.CompareTo(b);
            });

            NestChromosome chromosome = new NestChromosome();
            chromosome.Order = order.ToArray();
            chromosome.Rotate = new bool[parts.Count];
            for (int i = 0; i < parts.Count; i++)
            {
                chromosome.Rotate[i] = rotateLarge && parts[i].Height > parts[i].Width;
            }
            return chromosome;
        }

        private static void Evaluate(List<NestChromosome> population, List<NestPartInstance> parts)
        {
            foreach (NestChromosome chromosome in population)
            {
                chromosome.Result = Place(chromosome, parts);
                chromosome.Fitness = Fitness(chromosome.Result);
            }
        }

        private static int CompareChromosomes(NestChromosome a, NestChromosome b)
        {
            return a.Fitness.CompareTo(b.Fitness);
        }

        private static double Fitness(NestingResult result)
        {
            double maxUsedX = 0.0;
            double maxUsedY = 0.0;
            foreach (NestPlacement p in result.Placements)
            {
                maxUsedX = Math.Max(maxUsedX, p.X + p.Width);
                maxUsedY = Math.Max(maxUsedY, p.Y + p.Height);
            }
            return result.BoardCount * 1000000000.0 + maxUsedY * 1000.0 + maxUsedX - result.Utilization;
        }

        private static NestChromosome Tournament(List<NestChromosome> population, Random random)
        {
            NestChromosome best = null;
            for (int i = 0; i < 4; i++)
            {
                NestChromosome candidate = population[random.Next(population.Count)];
                if (best == null || candidate.Fitness < best.Fitness)
                {
                    best = candidate;
                }
            }
            return best;
        }

        private static NestChromosome Crossover(NestChromosome a, NestChromosome b, Random random)
        {
            int n = a.Order.Length;
            int cut1 = random.Next(n);
            int cut2 = random.Next(n);
            if (cut1 > cut2)
            {
                int tmp = cut1;
                cut1 = cut2;
                cut2 = tmp;
            }

            NestChromosome child = new NestChromosome();
            child.Order = new int[n];
            child.Rotate = new bool[n];
            for (int i = 0; i < n; i++)
            {
                child.Order[i] = -1;
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
                if (used[gene])
                {
                    continue;
                }
                child.Order[write] = gene;
                used[gene] = true;
                write = (write + 1) % n;
            }

            for (int i = 0; i < n; i++)
            {
                child.Rotate[i] = random.NextDouble() < 0.5 ? a.Rotate[i] : b.Rotate[i];
            }
            return child;
        }

        private static void Mutate(NestChromosome chromosome, Random random)
        {
            int n = chromosome.Order.Length;
            if (random.NextDouble() < MutationRate)
            {
                int a = random.Next(n);
                int b = random.Next(n);
                int tmp = chromosome.Order[a];
                chromosome.Order[a] = chromosome.Order[b];
                chromosome.Order[b] = tmp;
            }
            if (random.NextDouble() < MutationRate)
            {
                int a = random.Next(n);
                int b = random.Next(n);
                if (a > b)
                {
                    int tmp = a;
                    a = b;
                    b = tmp;
                }
                Array.Reverse(chromosome.Order, a, b - a + 1);
            }
            for (int i = 0; i < n; i++)
            {
                if (random.NextDouble() < RotationMutationRate)
                {
                    chromosome.Rotate[i] = !chromosome.Rotate[i];
                }
            }
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

        private static NestingResult Place(NestChromosome chromosome, List<NestPartInstance> parts)
        {
            List<List<NestFreeRect>> freeByBoard = new List<List<NestFreeRect>>();
            List<NestPlacement> placements = new List<NestPlacement>();
            double totalArea = 0.0;
            foreach (NestPartInstance part in parts)
            {
                totalArea += part.Width * part.Height;
            }

            foreach (int partIndex in chromosome.Order)
            {
                NestPartInstance part = parts[partIndex];
                NestPlacement placement = TryPlaceInBoards(part, chromosome.Rotate[partIndex], freeByBoard);
                if (placement == null)
                {
                    List<NestFreeRect> free = new List<NestFreeRect>();
                    free.Add(new NestFreeRect(0.0, 0.0, BoardWidth, BoardHeight));
                    freeByBoard.Add(free);
                    placement = TryPlaceInBoard(part, chromosome.Rotate[partIndex], free, freeByBoard.Count - 1);
                }

                if (placement == null)
                {
                    throw new InvalidOperationException("Part cannot fit on board: " + part.Name);
                }

                placements.Add(placement);
            }

            return new NestingResult(placements, freeByBoard.Count, BoardWidth, BoardHeight, totalArea);
        }

        private static NestPlacement TryPlaceInBoards(NestPartInstance part, bool preferRotate, List<List<NestFreeRect>> freeByBoard)
        {
            for (int i = 0; i < freeByBoard.Count; i++)
            {
                NestPlacement placement = TryPlaceInBoard(part, preferRotate, freeByBoard[i], i);
                if (placement != null)
                {
                    return placement;
                }
            }
            return null;
        }

        private static NestPlacement TryPlaceInBoard(NestPartInstance part, bool preferRotate, List<NestFreeRect> freeRects, int boardIndex)
        {
            PlacementCandidate best = null;
            TryFindCandidate(part, preferRotate, false, freeRects, ref best);
            TryFindCandidate(part, preferRotate, true, freeRects, ref best);
            if (best == null)
            {
                return null;
            }

            NestFreeRect chosen = freeRects[best.FreeRectIndex];
            freeRects.RemoveAt(best.FreeRectIndex);
            SplitFreeRect(freeRects, chosen, best.Width, best.Height);
            PruneFreeRects(freeRects);

            return new NestPlacement
            {
                Part = part,
                BoardIndex = boardIndex,
                X = chosen.X,
                Y = chosen.Y,
                Width = best.Width,
                Height = best.Height,
                Rotated = best.Rotated
            };
        }

        private static void TryFindCandidate(NestPartInstance part, bool preferRotate, bool rotate, List<NestFreeRect> freeRects, ref PlacementCandidate best)
        {
            bool rotated = rotate ^ preferRotate;
            double w = rotated ? part.Height : part.Width;
            double h = rotated ? part.Width : part.Height;
            for (int i = 0; i < freeRects.Count; i++)
            {
                NestFreeRect r = freeRects[i];
                if (w <= r.Width + 1e-6 && h <= r.Height + 1e-6)
                {
                    double leftover = r.Width * r.Height - w * h;
                    double shortSide = Math.Min(r.Width - w, r.Height - h);
                    double score = leftover * 10000.0 + shortSide;
                    if (best == null || score < best.Score)
                    {
                        best = new PlacementCandidate(i, w, h, rotated, score);
                    }
                }
            }
        }

        private static void SplitFreeRect(List<NestFreeRect> freeRects, NestFreeRect used, double usedWidth, double usedHeight)
        {
            double rightWidth = used.Width - usedWidth;
            double topHeight = used.Height - usedHeight;
            if (rightWidth > 1e-6)
            {
                freeRects.Add(new NestFreeRect(used.X + usedWidth, used.Y, rightWidth, usedHeight));
            }
            if (topHeight > 1e-6)
            {
                freeRects.Add(new NestFreeRect(used.X, used.Y + usedHeight, used.Width, topHeight));
            }
        }

        private static void PruneFreeRects(List<NestFreeRect> freeRects)
        {
            for (int i = freeRects.Count - 1; i >= 0; i--)
            {
                if (freeRects[i].Width <= 1e-6 || freeRects[i].Height <= 1e-6)
                {
                    freeRects.RemoveAt(i);
                    continue;
                }
                for (int j = freeRects.Count - 1; j >= 0; j--)
                {
                    if (i == j)
                    {
                        continue;
                    }
                    if (Contains(freeRects[j], freeRects[i]))
                    {
                        freeRects.RemoveAt(i);
                        break;
                    }
                }
            }
        }

        private static bool Contains(NestFreeRect a, NestFreeRect b)
        {
            return b.X >= a.X - 1e-6 &&
                b.Y >= a.Y - 1e-6 &&
                b.X + b.Width <= a.X + a.Width + 1e-6 &&
                b.Y + b.Height <= a.Y + a.Height + 1e-6;
        }

        private sealed class PlacementCandidate
        {
            public readonly int FreeRectIndex;
            public readonly double Width;
            public readonly double Height;
            public readonly bool Rotated;
            public readonly double Score;

            public PlacementCandidate(int freeRectIndex, double width, double height, bool rotated, double score)
            {
                FreeRectIndex = freeRectIndex;
                Width = width;
                Height = height;
                Rotated = rotated;
                Score = score;
            }
        }
    }

    internal static class NestingDrawer
    {
        public static void Draw(Document doc, NestingResult result, Point3d origin)
        {
            Database db = doc.Database;
            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                EnsureLayer(db, tr, "NEST_BOARD", 7);
                EnsureLayer(db, tr, "NEST_PART", 3);
                EnsureLayer(db, tr, "NEST_TEXT", 2);

                BlockTable blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                double gap = 350.0;

                for (int board = 0; board < result.BoardCount; board++)
                {
                    double boardX = origin.X;
                    double boardY = origin.Y - board * (result.BoardHeight + gap);
                    AppendPolyline(modelSpace, tr, Rect(boardX, boardY, result.BoardWidth, result.BoardHeight), "NEST_BOARD", 7);
                    AppendText(modelSpace, tr, new Point3d(boardX, boardY + result.BoardHeight + 80.0, 0.0), 120.0, "Board " + (board + 1).ToString(CultureInfo.InvariantCulture) + " 6000 x 1500", "NEST_TEXT", 2, 0.0);
                }

                foreach (NestPlacement placement in result.Placements)
                {
                    double boardX = origin.X;
                    double boardY = origin.Y - placement.BoardIndex * (result.BoardHeight + gap);
                    double x = boardX + placement.X;
                    double y = boardY + placement.Y;
                    AppendPolyline(modelSpace, tr, Rect(x, y, placement.Width, placement.Height), "NEST_PART", placement.Part.ColorIndex);
                    string label = placement.Part.Name + "-" + placement.Part.Number.ToString(CultureInfo.InvariantCulture);
                    if (placement.Rotated)
                    {
                        label += " R";
                    }
                    AppendText(modelSpace, tr, new Point3d(x + placement.Width / 2.0, y + placement.Height / 2.0, 0.0), 70.0, label, "NEST_TEXT", placement.Part.ColorIndex, 0.0);
                }

                string summary = "Sheets: " + result.BoardCount.ToString(CultureInfo.InvariantCulture) +
                    "  Utilization: " + (result.Utilization * 100.0).ToString("0.00", CultureInfo.InvariantCulture) + "%" +
                    "  Parts: " + result.Placements.Count.ToString(CultureInfo.InvariantCulture);
                AppendText(modelSpace, tr, new Point3d(origin.X, origin.Y + result.BoardHeight + 260.0, 0.0), 140.0, summary, "NEST_TEXT", 2, 0.0);
                tr.Commit();
            }

            doc.Editor.Regen();
        }

        private static Point2dCollection Rect(double x, double y, double width, double height)
        {
            Point2dCollection points = new Point2dCollection();
            points.Add(new Point2d(x, y));
            points.Add(new Point2d(x + width, y));
            points.Add(new Point2d(x + width, y + height));
            points.Add(new Point2d(x, y + height));
            return points;
        }

        private static void EnsureLayer(Database db, Transaction tr, string name, short colorIndex)
        {
            LayerTable layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (layerTable.Has(name))
            {
                return;
            }

            layerTable.UpgradeOpen();
            LayerTableRecord record = new LayerTableRecord();
            record.Name = name;
            record.Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex);
            layerTable.Add(record);
            tr.AddNewlyCreatedDBObject(record, true);
        }

        private static void AppendPolyline(BlockTableRecord modelSpace, Transaction tr, Point2dCollection points, string layer, short color)
        {
            Polyline polyline = new Polyline(points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                polyline.AddVertexAt(i, points[i], 0.0, 0.0, 0.0);
            }
            polyline.Closed = true;
            polyline.Layer = layer;
            polyline.ColorIndex = color;
            modelSpace.AppendEntity(polyline);
            tr.AddNewlyCreatedDBObject(polyline, true);
        }

        private static void AppendText(BlockTableRecord modelSpace, Transaction tr, Point3d point, double height, string value, string layer, short color, double rotation)
        {
            DBText text = new DBText();
            text.Position = point;
            text.Height = height;
            text.TextString = value;
            text.Layer = layer;
            text.ColorIndex = color;
            text.Rotation = rotation;
            text.HorizontalMode = TextHorizontalMode.TextCenter;
            text.VerticalMode = TextVerticalMode.TextVerticalMid;
            text.AlignmentPoint = point;
            modelSpace.AppendEntity(text);
            tr.AddNewlyCreatedDBObject(text, true);
        }
    }

    internal class SpecBuilder
    {
        private readonly List<object> _layers = new List<object>();
        private readonly List<object> _entities = new List<object>();

        public void Layer(string name, int color)
        {
            Dictionary<string, object> layer = new Dictionary<string, object>();
            layer["name"] = name;
            layer["color"] = color;
            _layers.Add(layer);
        }

        public void Line(string layer, double x1, double y1, double x2, double y2)
        {
            Dictionary<string, object> entity = Entity("line", layer);
            entity["x1"] = x1;
            entity["y1"] = y1;
            entity["x2"] = x2;
            entity["y2"] = y2;
            _entities.Add(entity);
        }

        public void Circle(string layer, double x, double y, double radius)
        {
            Dictionary<string, object> entity = Entity("circle", layer);
            entity["x"] = x;
            entity["y"] = y;
            entity["radius"] = radius;
            _entities.Add(entity);
        }

        public void Text(string layer, double x, double y, double height, string text, double rotation)
        {
            Dictionary<string, object> entity = Entity("text", layer);
            entity["x"] = x;
            entity["y"] = y;
            entity["height"] = height;
            entity["text"] = text;
            entity["rotation"] = rotation;
            _entities.Add(entity);
        }

        public void Polyline(string layer, double[,] points, bool closed)
        {
            Dictionary<string, object> entity = Entity("polyline", layer);
            List<object> pairs = new List<object>();
            for (int i = 0; i < points.GetLength(0); i++)
            {
                pairs.Add(new List<object> { points[i, 0], points[i, 1] });
            }
            entity["points"] = pairs;
            entity["closed"] = closed;
            _entities.Add(entity);
        }

        public Dictionary<string, object> Build()
        {
            Dictionary<string, object> spec = new Dictionary<string, object>();
            spec["units"] = "mm";
            spec["layers"] = _layers;
            spec["entities"] = _entities;
            return spec;
        }

        private static Dictionary<string, object> Entity(string type, string layer)
        {
            Dictionary<string, object> entity = new Dictionary<string, object>();
            entity["type"] = type;
            entity["layer"] = layer;
            return entity;
        }
    }

    internal static class MiniJson
    {
        public static object Parse(string json)
        {
            Parser parser = new Parser(json);
            object value = parser.ParseValue();
            parser.SkipWhiteSpace();
            if (!parser.End)
            {
                throw new InvalidOperationException("Unexpected trailing JSON content.");
            }
            return value;
        }

        private sealed class Parser
        {
            private readonly string _text;
            private int _index;

            public Parser(string text)
            {
                _text = text;
            }

            public bool End
            {
                get { return _index >= _text.Length; }
            }

            public object ParseValue()
            {
                SkipWhiteSpace();
                if (End)
                {
                    throw new InvalidOperationException("Unexpected end of JSON.");
                }

                char ch = _text[_index];
                if (ch == '{')
                {
                    return ParseObject();
                }
                if (ch == '[')
                {
                    return ParseArray();
                }
                if (ch == '"')
                {
                    return ParseString();
                }
                if (ch == '-' || char.IsDigit(ch))
                {
                    return ParseNumber();
                }
                if (Match("true"))
                {
                    return true;
                }
                if (Match("false"))
                {
                    return false;
                }
                if (Match("null"))
                {
                    return null;
                }

                throw new InvalidOperationException("Unexpected JSON token at index " + _index.ToString(CultureInfo.InvariantCulture) + ".");
            }

            public void SkipWhiteSpace()
            {
                while (!End && char.IsWhiteSpace(_text[_index]))
                {
                    _index++;
                }
            }

            private Dictionary<string, object> ParseObject()
            {
                Expect('{');
                Dictionary<string, object> obj = new Dictionary<string, object>();
                SkipWhiteSpace();
                if (TryConsume('}'))
                {
                    return obj;
                }

                while (true)
                {
                    SkipWhiteSpace();
                    string key = ParseString();
                    SkipWhiteSpace();
                    Expect(':');
                    obj[key] = ParseValue();
                    SkipWhiteSpace();
                    if (TryConsume('}'))
                    {
                        return obj;
                    }
                    Expect(',');
                }
            }

            private List<object> ParseArray()
            {
                Expect('[');
                List<object> array = new List<object>();
                SkipWhiteSpace();
                if (TryConsume(']'))
                {
                    return array;
                }

                while (true)
                {
                    array.Add(ParseValue());
                    SkipWhiteSpace();
                    if (TryConsume(']'))
                    {
                        return array;
                    }
                    Expect(',');
                }
            }

            private string ParseString()
            {
                Expect('"');
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                while (!End)
                {
                    char ch = _text[_index++];
                    if (ch == '"')
                    {
                        return sb.ToString();
                    }
                    if (ch == '\\')
                    {
                        if (End)
                        {
                            throw new InvalidOperationException("Invalid JSON escape.");
                        }
                        char esc = _text[_index++];
                        if (esc == '"' || esc == '\\' || esc == '/')
                        {
                            sb.Append(esc);
                        }
                        else if (esc == 'b')
                        {
                            sb.Append('\b');
                        }
                        else if (esc == 'f')
                        {
                            sb.Append('\f');
                        }
                        else if (esc == 'n')
                        {
                            sb.Append('\n');
                        }
                        else if (esc == 'r')
                        {
                            sb.Append('\r');
                        }
                        else if (esc == 't')
                        {
                            sb.Append('\t');
                        }
                        else if (esc == 'u')
                        {
                            if (_index + 4 > _text.Length)
                            {
                                throw new InvalidOperationException("Invalid JSON unicode escape.");
                            }
                            string hex = _text.Substring(_index, 4);
                            sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            _index += 4;
                        }
                        else
                        {
                            throw new InvalidOperationException("Invalid JSON escape: " + esc);
                        }
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                }

                throw new InvalidOperationException("Unterminated JSON string.");
            }

            private double ParseNumber()
            {
                int start = _index;
                if (_text[_index] == '-')
                {
                    _index++;
                }
                while (!End && char.IsDigit(_text[_index]))
                {
                    _index++;
                }
                if (!End && _text[_index] == '.')
                {
                    _index++;
                    while (!End && char.IsDigit(_text[_index]))
                    {
                        _index++;
                    }
                }
                if (!End && (_text[_index] == 'e' || _text[_index] == 'E'))
                {
                    _index++;
                    if (!End && (_text[_index] == '+' || _text[_index] == '-'))
                    {
                        _index++;
                    }
                    while (!End && char.IsDigit(_text[_index]))
                    {
                        _index++;
                    }
                }
                return double.Parse(_text.Substring(start, _index - start), CultureInfo.InvariantCulture);
            }

            private bool Match(string token)
            {
                if (_index + token.Length > _text.Length)
                {
                    return false;
                }
                if (string.Compare(_text, _index, token, 0, token.Length, StringComparison.Ordinal) == 0)
                {
                    _index += token.Length;
                    return true;
                }
                return false;
            }

            private void Expect(char expected)
            {
                SkipWhiteSpace();
                if (End || _text[_index] != expected)
                {
                    throw new InvalidOperationException("Expected '" + expected + "' at JSON index " + _index.ToString(CultureInfo.InvariantCulture) + ".");
                }
                _index++;
            }

            private bool TryConsume(char ch)
            {
                SkipWhiteSpace();
                if (!End && _text[_index] == ch)
                {
                    _index++;
                    return true;
                }
                return false;
            }
        }
    }
}
