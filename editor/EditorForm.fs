/// FsRocket Level Editor — WinForms UI shell.
/// Thin layer over FsRocketEditor.EditorCore (all real logic lives there and is
/// unit-tested separately). Paints the 320x400 map scaled by an integer zoom,
/// handles brush/fill/line/rect tools, image import and .LEV save/load.
module FsRocketEditor.EditorForm

open System
open System.Drawing
open System.Drawing.Imaging
open System.Runtime.InteropServices
open System.Windows.Forms
open FsRocket.Physics
open FsRocket.Terrain
open FsRocketEditor.EditorCore

type Tool = Brush | Fill | Line | Rect

/// A Panel with double buffering enabled so the scaled map repaints flicker-free.
type private CanvasPanel() =
    inherit Panel()
    do
        base.DoubleBuffered <- true
        base.SetStyle(ControlStyles.OptimizedDoubleBuffer
                      ||| ControlStyles.AllPaintingInWmPaint
                      ||| ControlStyles.UserPaint, true)

// ─── Color-guide / help text (also the "instructions" requested) ───────────

let private colorGuideText =
    "TERRAIN COLOURS — what each pixel MEANS to the game\n\
     ───────────────────────────────────────────────\n\
     • VOID  (black, palette 0x00) — empty sky / flyable space. Carve caves and\n\
       tunnels with the Void brush. Ships and bullets pass freely.\n\
     • WALL  (any other colour) — SOLID, destructible terrain. The Wall brush\n\
       paints one rock colour, but the game treats EVERY non-special colour as\n\
       wall, which is why imported images become solid ground.\n\
     • WATER (palette 0x27) — penetrable, but applies friction/drag. Paint with\n\
       the Water brush.\n\
     • BASE  (palette 0x5C–0x5F) — landing pad. Indestructible. Ships land here\n\
       to stop, recharge and switch weapons. SPAWN POINTS are derived from bases\n\
       with empty void directly above them — toggle 'Show spawns' to preview.\n\n\
     IMPORTING AN IMAGE — two colour modes\n\
     ─────────────────────────────────────\n\
     WALLS ONLY: every pixel maps to the nearest SOLID wall colour (never water,\n\
       base, or pure black) so the whole picture becomes solid terrain. Import a\n\
       texture (e.g. a brick wall), then carve caves with the Void brush and place\n\
       landing pads with the Base brush. Tick 'carve dark pixels' to turn dark\n\
       areas into caves on import.\n\
     KEEP COLOURS: paint a whole level in any image editor using these KEY colours\n\
       and import it ready to play —\n\
         black  #000000  = void / caves\n\
         blue   #0099FF  = water\n\
         green  #30C060  = base / landing pad\n\
         anything else   = solid wall (its colour is preserved)\n\
       Remember a base needs empty void just above it to become a spawn point."

let private controlsText =
    "TOOLS         B Brush   F Fill   L Line   R Rectangle\n\
     MATERIALS     1 Void    2 Wall   3 Water  4 Base\n\
     BRUSH SIZE    [  smaller     ]  larger\n\
     ZOOM          -  out         +  in\n\
     SPAWNS        G  toggle spawn-point preview\n\
     EDIT          Ctrl+Z undo   Ctrl+Y redo\n\
     FILE          Ctrl+N new    Ctrl+O open   Ctrl+S save\n\
     PUBLISH       Ctrl+P copy the saved level into the game's folder\n\n\
     MOUSE         Left = paint with current material\n\
                   Right = erase (paint Void) regardless of material\n\
     For Line/Rectangle: press, drag, release."

// ─── Import options dialog ─────────────────────────────────────────────────

let private promptImport (owner: IWin32Window) : ImportOptions option =
    use dlg = new Form(Text = "Import image", FormBorderStyle = FormBorderStyle.FixedDialog,
                       StartPosition = FormStartPosition.CenterParent,
                       MinimizeBox = false, MaximizeBox = false,
                       ClientSize = Size(380, 320))
    let fitLabel = new Label(Text = "Fit to 320x400:", Left = 12, Top = 18, Width = 100)
    let fitCombo = new ComboBox(Left = 130, Top = 15, Width = 228,
                                DropDownStyle = ComboBoxStyle.DropDownList)
    fitCombo.Items.AddRange([| "Stretch (distort to fill)"
                               "Fit (keep aspect, letterbox)"
                               "Center (native size, centred)" |])
    fitCombo.SelectedIndex <- 0

    let modeLabel = new Label(Text = "Colours:", Left = 12, Top = 52, Width = 100)
    let modeCombo = new ComboBox(Left = 130, Top = 49, Width = 228,
                                 DropDownStyle = ComboBoxStyle.DropDownList)
    modeCombo.Items.AddRange([| "Walls only — everything solid"
                                "Keep colours — black/blue/green = void/water/base" |])
    modeCombo.SelectedIndex <- 0

    let keyInfo = new Label(Left = 12, Top = 82, Width = 356, Height = 48,
                            ForeColor = Color.DimGray,
                            Text = "Keep-colours uses these key colours in the source image:\n"
                                   + "black #000000 = void/caves,  #0099FF = water,  #30C060 = base.")

    // Keep-colours only: how close a pixel must be to a key colour to snap to it.
    let tolLabel = new Label(Text = "Key-colour tolerance (0-96):", Left = 30, Top = 134, Width = 180)
    let tolNud = new NumericUpDown(Left = 235, Top = 131, Width = 80,
                                   Minimum = 0m, Maximum = 96m, Value = decimal DefaultKeyTolerance,
                                   Enabled = false)
    // Walls-only: carve dark pixels into caves.
    let caveChk = new CheckBox(Text = "Carve dark pixels into caves (Void)",
                               Left = 12, Top = 170, Width = 340)
    let thLabel = new Label(Text = "Darkness threshold (0-255):", Left = 30, Top = 202, Width = 180)
    let thNud = new NumericUpDown(Left = 235, Top = 199, Width = 80,
                                  Minimum = 0m, Maximum = 255m, Value = 40m, Enabled = false)
    caveChk.CheckedChanged.Add(fun _ -> thNud.Enabled <- caveChk.Checked && caveChk.Enabled)
    // Show the controls relevant to the chosen colour mode.
    modeCombo.SelectedIndexChanged.Add(fun _ ->
        let solid = modeCombo.SelectedIndex = 0
        caveChk.Enabled <- solid
        thNud.Enabled <- solid && caveChk.Checked
        tolNud.Enabled <- not solid)

    let ok = new Button(Text = "Import", Left = 188, Top = 270, Width = 80, DialogResult = DialogResult.OK)
    let cancel = new Button(Text = "Cancel", Left = 278, Top = 270, Width = 80, DialogResult = DialogResult.Cancel)
    dlg.Controls.AddRange([| fitLabel :> Control; fitCombo; modeLabel; modeCombo; keyInfo
                             tolLabel; tolNud; caveChk; thLabel; thNud; ok; cancel |])
    dlg.AcceptButton <- ok
    dlg.CancelButton <- cancel
    if dlg.ShowDialog(owner) = DialogResult.OK then
        let fit = match fitCombo.SelectedIndex with 1 -> Fit | 2 -> Center | _ -> Stretch
        let colors = if modeCombo.SelectedIndex = 1 then PreserveColors else SolidWalls
        let th = if colors = SolidWalls && caveChk.Checked then int thNud.Value else -1
        Some { Fit = fit; Colors = colors; VoidThreshold = th; KeyTolerance = int tolNud.Value }
    else None

// ─── Read a source bitmap into a flat ARGB sampler ─────────────────────────

let private bitmapSampler (src: Bitmap) : int * int * (int -> int -> int) =
    let w, h = src.Width, src.Height
    let data = src.LockBits(Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb)
    let strideInts = data.Stride / 4
    let buf = Array.zeroCreate<int> (strideInts * h)
    Marshal.Copy(data.Scan0, buf, 0, buf.Length)
    src.UnlockBits data
    w, h, (fun sx sy -> buf[sy * strideInts + sx])

// ─── Main editor form ──────────────────────────────────────────────────────

type EditorForm() as this =
    inherit Form()

    // ── Document state ──
    let mutable pixels : byte array = Array.create (MapWidth * MapHeight) VoidMat
    let mutable viewports = (0, 0, MapWidth - 1, MapHeight - 1)
    let mutable filePath = ""        // "" = never saved
    let mutable levelName = "UNTITLED"
    let mutable dirty = false

    // ── "Publish to game" target (remembered between sessions) ──
    let settingsPath = IO.Path.Combine(Application.StartupPath, "FsRocketEditor.settings")
    let mutable gameLevelDir =
        try if IO.File.Exists settingsPath then (IO.File.ReadAllText settingsPath).Trim() else ""
        with _ -> ""

    // ── Tool state ──
    let mutable tool = Brush
    let mutable material = MWall
    let mutable brushRadius = 1      // brush diameter = 2*radius + 1
    let mutable zoom = 2
    let mutable showSpawns = false

    // ── Interaction state ──
    let mutable painting = false
    let mutable strokeValue = WallMat
    let mutable lastPx = (0, 0)
    let mutable dragStart = (0, 0)
    let mutable dragCur = (0, 0)

    // ── Undo / redo (bounded snapshot stacks) ──
    let undoList = System.Collections.Generic.List<byte array>()
    let redoList = System.Collections.Generic.List<byte array>()
    let undoLimit = 40

    // ── Render cache ──
    let bmp = new Bitmap(MapWidth, MapHeight, PixelFormat.Format32bppArgb)
    let mutable bmpBuf : int array = Array.empty   // reused scratch buffer for bitmap upload
    let mutable bmpDirty = true
    let mutable cachedSpawns : SpawnPoint array = [||]

    // ── Controls ──
    let canvas = new CanvasPanel()
    let scroller = new Panel(Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(0x20,0x20,0x28))
    let statusStrip = new StatusStrip()
    let coordLabel = new ToolStripStatusLabel(Text = "x: -  y: -", AutoSize = false, Width = 120)
    let infoLabel = new ToolStripStatusLabel(Text = "", Spring = true, TextAlign = ContentAlignment.MiddleLeft)
    let toolStrip = new ToolStrip()
    let menuStrip = new MenuStrip()

    // Tool / material buttons kept for radio-style check management.
    let toolButtons = System.Collections.Generic.Dictionary<Tool, ToolStripButton>()
    let matButtons = System.Collections.Generic.Dictionary<Material, ToolStripButton>()
    let sizeCombo = new ToolStripComboBox()
    let zoomLabel = new ToolStripLabel(Text = "Zoom 2x")
    let spawnBtn = new ToolStripButton("Show spawns (G)", CheckOnClick = true)

    // ── Helpers ──

    let buildLevel () : LevelData =
        { Pixels = pixels; Viewports = viewports; SpawnPoints = [||]; Name = levelName }

    let updateTitle () =
        this.Text <- sprintf "FsRocket Level Editor — %s%s" levelName (if dirty then " *" else "")

    let updateInfo () =
        let matName = match material with MVoid -> "Void" | MWall -> "Wall" | MWater -> "Water" | MBase -> "Base"
        let toolName = match tool with Brush -> "Brush" | Fill -> "Fill" | Line -> "Line" | Rect -> "Rect"
        infoLabel.Text <- sprintf "  Tool: %s   Material: %s   Brush: %dpx   (F1 = colour guide)"
                                  toolName matName (2 * brushRadius + 1)

    let refreshCanvas () =
        bmpDirty <- true
        canvas.Invalidate()

    let resizeCanvas () =
        canvas.Size <- Size(MapWidth * zoom, MapHeight * zoom)
        canvas.Invalidate()

    let pushUndo () =
        redoList.Clear()
        undoList.Add(Array.copy pixels)
        if undoList.Count > undoLimit then undoList.RemoveAt 0
        if not dirty then dirty <- true; updateTitle ()

    let markDirty () =
        if not dirty then dirty <- true; updateTitle ()

    let doUndo () =
        if undoList.Count > 0 then
            redoList.Add(Array.copy pixels)
            pixels <- undoList[undoList.Count - 1]
            undoList.RemoveAt(undoList.Count - 1)
            markDirty (); refreshCanvas ()

    let doRedo () =
        if redoList.Count > 0 then
            undoList.Add(Array.copy pixels)
            pixels <- redoList[redoList.Count - 1]
            redoList.RemoveAt(redoList.Count - 1)
            markDirty (); refreshCanvas ()

    /// Rebuild the 320x400 display bitmap (and spawn cache) from the pixels.
    /// Called on every brush move during a stroke, so it must stay cheap: the
    /// pixel buffer is reused across calls, and the spawn scan only runs when the
    /// spawn overlay is actually visible.
    let updateBitmap () =
        if bmpDirty then
            let data = bmp.LockBits(Rectangle(0, 0, MapWidth, MapHeight),
                                    ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb)
            let strideInts = data.Stride / 4
            let needed = strideInts * MapHeight
            if bmpBuf.Length <> needed then bmpBuf <- Array.zeroCreate needed
            for y in 0 .. MapHeight - 1 do
                for x in 0 .. MapWidth - 1 do
                    bmpBuf[y * strideInts + x] <- displayArgb pixels[y * MapWidth + x]
            Marshal.Copy(bmpBuf, 0, data.Scan0, needed)
            bmp.UnlockBits data
            if showSpawns then cachedSpawns <- findSpawnPoints pixels
            bmpDirty <- false

    let confirmDiscard () =
        if not dirty then true
        else
            match MessageBox.Show("Discard unsaved changes?", "FsRocket Level Editor",
                                  MessageBoxButtons.YesNo, MessageBoxIcon.Warning) with
            | DialogResult.Yes -> true
            | _ -> false

    // ── Tool / material selection ──

    let selectTool t =
        tool <- t
        for kv in toolButtons do kv.Value.Checked <- (kv.Key = t)
        updateInfo ()

    let selectMaterial m =
        material <- m
        for kv in matButtons do kv.Value.Checked <- (kv.Key = m)
        updateInfo ()

    let setZoom z =
        let nz = max 1 (min 8 z)
        if nz <> zoom then
            zoom <- nz
            zoomLabel.Text <- sprintf "Zoom %dx" zoom
            resizeCanvas ()

    // ── Pixel coordinate from a mouse event on the canvas ──
    let toPixel (e: MouseEventArgs) =
        let x = e.X / zoom
        let y = e.Y / zoom
        (max 0 (min (MapWidth - 1) x), max 0 (min (MapHeight - 1) y))

    // ── File operations ──

    let newLevel () =
        if confirmDiscard () then
            let lv = newBlankLevel "UNTITLED"
            pixels <- lv.Pixels
            viewports <- lv.Viewports
            filePath <- ""
            levelName <- "UNTITLED"
            dirty <- false
            undoList.Clear(); redoList.Clear()
            updateTitle (); refreshCanvas ()

    let openLevel () =
        if confirmDiscard () then
            use dlg = new OpenFileDialog(Filter = "AUTS level (*.LEV)|*.LEV|All files (*.*)|*.*")
            if dlg.ShowDialog(this) = DialogResult.OK then
                try
                    let lv = loadLevel dlg.FileName
                    pixels <- lv.Pixels
                    viewports <- lv.Viewports
                    filePath <- dlg.FileName
                    levelName <- lv.Name
                    dirty <- false
                    undoList.Clear(); redoList.Clear()
                    updateTitle (); refreshCanvas ()
                with ex ->
                    MessageBox.Show("Could not load level:\n" + ex.Message, "Error",
                                    MessageBoxButtons.OK, MessageBoxIcon.Error) |> ignore

    let saveToPath (path: string) =
        try
            saveLevel path (buildLevel ())
            filePath <- path
            levelName <- IO.Path.GetFileNameWithoutExtension path
            dirty <- false
            updateTitle ()
            true
        with ex ->
            MessageBox.Show("Could not save level:\n" + ex.Message, "Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error) |> ignore
            false

    let saveLevelAs () =
        use dlg = new SaveFileDialog(Filter = "AUTS level (*.LEV)|*.LEV",
                                     DefaultExt = "LEV", AddExtension = true,
                                     FileName = (if levelName = "" then "UNTITLED" else levelName) + ".LEV")
        if dlg.ShowDialog(this) = DialogResult.OK then saveToPath dlg.FileName |> ignore

    let saveCurrent () =
        if filePath = "" then saveLevelAs () else saveToPath filePath |> ignore

    let importImageFile () =
        use dlg = new OpenFileDialog(Filter = "Images (*.png;*.bmp;*.jpg;*.jpeg;*.gif)|*.png;*.bmp;*.jpg;*.jpeg;*.gif|All files (*.*)|*.*")
        if dlg.ShowDialog(this) = DialogResult.OK then
            match promptImport this with
            | None -> ()
            | Some opt ->
                try
                    use src = new Bitmap(dlg.FileName)
                    let w, h, sample = bitmapSampler src
                    let result = importImage w h sample opt
                    pushUndo ()
                    pixels <- result
                    refreshCanvas ()
                with ex ->
                    MessageBox.Show("Could not import image:\n" + ex.Message, "Error",
                                    MessageBoxButtons.OK, MessageBoxIcon.Error) |> ignore

    /// Ask for (and remember) the game's level folder. Returns true if chosen.
    let setGameFolder () =
        use dlg = new FolderBrowserDialog(
                    Description = "Select the game folder where its .LEV levels live "
                                  + "(the folder next to the FsRocket game executable).")
        if gameLevelDir <> "" then dlg.SelectedPath <- gameLevelDir
        if dlg.ShowDialog(this) = DialogResult.OK then
            gameLevelDir <- dlg.SelectedPath
            try IO.File.WriteAllText(settingsPath, gameLevelDir) with _ -> ()
            true
        else false

    /// Save the current level (if needed) and copy it into the game's level folder
    /// so it shows up in the game. Asks for the folder the first time and remembers it.
    let publishToGame () =
        saveCurrent ()                       // ensure latest edits are on disk
        if filePath = "" || dirty then ()    // user cancelled the save, or it failed
        else
            let haveDir = (gameLevelDir <> "" && IO.Directory.Exists gameLevelDir) || setGameFolder ()
            if haveDir && IO.Directory.Exists gameLevelDir then
                let fileName = IO.Path.GetFileName filePath
                let dest = IO.Path.Combine(gameLevelDir, fileName)
                if IO.Path.GetFullPath dest = IO.Path.GetFullPath filePath then
                    MessageBox.Show("This level is already in the game folder.", "Publish to game",
                                    MessageBoxButtons.OK, MessageBoxIcon.Information) |> ignore
                else
                    let proceed =
                        not (IO.File.Exists dest)
                        || MessageBox.Show(sprintf "%s already exists in the game folder. Overwrite?" fileName,
                                           "Publish to game", MessageBoxButtons.YesNo,
                                           MessageBoxIcon.Warning) = DialogResult.Yes
                    if proceed then
                        try
                            IO.File.Copy(filePath, dest, true)
                            MessageBox.Show(
                                sprintf "Published %s to:\n%s\n\nRestart the game to see it in the level list (switch with F5/F6)."
                                        fileName gameLevelDir,
                                "Publish to game", MessageBoxButtons.OK, MessageBoxIcon.Information) |> ignore
                        with ex ->
                            MessageBox.Show("Could not copy to the game folder:\n" + ex.Message, "Error",
                                            MessageBoxButtons.OK, MessageBoxIcon.Error) |> ignore

    // ── UI construction ──

    let makeSwatch (argb: int) =
        let b = new Bitmap(16, 16)
        use g = Graphics.FromImage b
        g.Clear(Color.FromArgb(argb))
        g.DrawRectangle(Pens.Black, 0, 0, 15, 15)
        b :> Image

    let addToolButton (t: Tool) (text: string) =
        let b = new ToolStripButton(text, null, (fun _ _ -> selectTool t))
        b.CheckOnClick <- false
        toolButtons[t] <- b
        toolStrip.Items.Add b |> ignore

    let addMatButton (m: Material) (text: string) (argb: int) =
        let b = new ToolStripButton(text, makeSwatch argb, (fun _ _ -> selectMaterial m))
        b.CheckOnClick <- false
        b.ImageAlign <- ContentAlignment.MiddleLeft
        b.TextImageRelation <- TextImageRelation.ImageBeforeText
        matButtons[m] <- b
        toolStrip.Items.Add b |> ignore

    do
        // ── Menu ──
        let fileMenu = new ToolStripMenuItem("&File")
        let mi (text: string) (sc: Keys) (action: unit -> unit) : ToolStripItem =
            let item = new ToolStripMenuItem(text, null, (fun _ _ -> action ()))
            if sc <> Keys.None then item.ShortcutKeys <- sc
            item :> ToolStripItem
        fileMenu.DropDownItems.AddRange([|
            mi "&New" (Keys.Control ||| Keys.N) newLevel
            mi "&Open..." (Keys.Control ||| Keys.O) openLevel
            mi "&Save" (Keys.Control ||| Keys.S) saveCurrent
            mi "Save &As..." Keys.None saveLevelAs
            (new ToolStripSeparator() :> ToolStripItem)
            mi "&Publish to game" (Keys.Control ||| Keys.P) publishToGame
            mi "Set &game folder..." Keys.None (fun () -> setGameFolder () |> ignore)
            (new ToolStripSeparator() :> ToolStripItem)
            mi "&Import image..." Keys.None importImageFile
            (new ToolStripSeparator() :> ToolStripItem)
            mi "E&xit" Keys.None (fun () -> this.Close())
        |])
        let editMenu = new ToolStripMenuItem("&Edit")
        editMenu.DropDownItems.AddRange([|
            mi "&Undo" (Keys.Control ||| Keys.Z) doUndo
            mi "&Redo" (Keys.Control ||| Keys.Y) doRedo
        |])
        let helpMenu = new ToolStripMenuItem("&Help")
        helpMenu.DropDownItems.AddRange([|
            mi "&Colour guide / instructions" Keys.F1
               (fun () -> MessageBox.Show(colorGuideText, "Colour guide", MessageBoxButtons.OK, MessageBoxIcon.Information) |> ignore)
            mi "&Controls" Keys.None
               (fun () -> MessageBox.Show(controlsText, "Controls", MessageBoxButtons.OK, MessageBoxIcon.Information) |> ignore)
        |])
        menuStrip.Items.AddRange([| fileMenu :> ToolStripItem; editMenu :> ToolStripItem; helpMenu :> ToolStripItem |])

        // ── Tool strip ──
        addToolButton Brush "Brush (B)"
        addToolButton Fill "Fill (F)"
        addToolButton Line "Line (L)"
        addToolButton Rect "Rect (R)"
        toolStrip.Items.Add(new ToolStripSeparator()) |> ignore
        addMatButton MVoid "Void" (vgaPalette[int VoidMat])
        addMatButton MWall "Wall" (vgaPalette[int WallMat])
        addMatButton MWater "Water" (vgaPalette[int WaterMat])
        addMatButton MBase "Base" BasePadArgb
        toolStrip.Items.Add(new ToolStripSeparator()) |> ignore
        toolStrip.Items.Add(new ToolStripLabel("Size")) |> ignore
        sizeCombo.DropDownStyle <- ComboBoxStyle.DropDownList
        sizeCombo.Items.AddRange([| "1"; "3"; "5"; "7"; "9"; "15"; "25" |])
        sizeCombo.SelectedIndex <- 1   // diameter 3 -> radius 1
        sizeCombo.Width <- 50
        sizeCombo.SelectedIndexChanged.Add(fun _ ->
            let d = Int32.Parse(string sizeCombo.SelectedItem)
            brushRadius <- (d - 1) / 2
            updateInfo ())
        toolStrip.Items.Add sizeCombo |> ignore
        toolStrip.Items.Add(new ToolStripSeparator()) |> ignore
        let zoomOut = new ToolStripButton("-", null, (fun _ _ -> setZoom (zoom - 1)))
        let zoomIn = new ToolStripButton("+", null, (fun _ _ -> setZoom (zoom + 1)))
        toolStrip.Items.Add zoomOut |> ignore
        toolStrip.Items.Add zoomLabel |> ignore
        toolStrip.Items.Add zoomIn |> ignore
        toolStrip.Items.Add(new ToolStripSeparator()) |> ignore
        spawnBtn.CheckedChanged.Add(fun _ -> showSpawns <- spawnBtn.Checked; refreshCanvas ())
        toolStrip.Items.Add spawnBtn |> ignore

        // ── Status strip ──
        statusStrip.Items.AddRange([| coordLabel :> ToolStripItem; infoLabel :> ToolStripItem |])

        // ── Canvas ──
        canvas.Size <- Size(MapWidth * zoom, MapHeight * zoom)
        canvas.Paint.Add(this.OnCanvasPaint)
        canvas.MouseDown.Add(this.OnCanvasMouseDown)
        canvas.MouseMove.Add(this.OnCanvasMouseMove)
        canvas.MouseUp.Add(this.OnCanvasMouseUp)
        canvas.MouseLeave.Add(fun _ -> coordLabel.Text <- "x: -  y: -")
        scroller.Controls.Add canvas

        // ── Form composition (z-order: Fill first, then bottom, then top bars) ──
        this.Controls.Add scroller
        this.Controls.Add statusStrip
        this.Controls.Add toolStrip
        this.Controls.Add menuStrip
        this.MainMenuStrip <- menuStrip

        this.Text <- "FsRocket Level Editor"
        this.ClientSize <- Size(820, 720)
        this.KeyPreview <- true
        this.BackColor <- Color.FromArgb(0x20, 0x20, 0x28)

        selectTool Brush
        selectMaterial MWall
        updateInfo ()
        updateTitle ()

    // ── Canvas painting ──

    member private _.OnCanvasPaint(e: PaintEventArgs) =
        updateBitmap ()
        let g = e.Graphics
        g.InterpolationMode <- Drawing2D.InterpolationMode.NearestNeighbor
        g.PixelOffsetMode <- Drawing2D.PixelOffsetMode.Half
        g.DrawImage(bmp, Rectangle(0, 0, MapWidth * zoom, MapHeight * zoom))

        // Spawn-point overlay
        if showSpawns then
            use pen = new Pen(Color.Magenta, 1.0f)
            use brush = new SolidBrush(Color.FromArgb(0x80, Color.Magenta))
            for sp in cachedSpawns do
                let x = sp.X * zoom
                let y = sp.Y * zoom
                let w = max 1 (sp.Width * zoom)
                g.FillRectangle(brush, x, y, w, zoom)            // pad span marker
                let cx = x + w / 2
                g.DrawLine(pen, cx, y - 6, cx, y + 6)            // spawn cross
                g.DrawLine(pen, cx - 6, y, cx + 6, y)

        // Rubber-band preview for Line / Rect while dragging
        if painting && (tool = Line || tool = Rect) then
            let (sx, sy) = dragStart
            let (cx, cy) = dragCur
            use pen = new Pen(Color.Yellow, 1.0f)
            pen.DashStyle <- Drawing2D.DashStyle.Dot
            match tool with
            | Line -> g.DrawLine(pen, sx * zoom + zoom / 2, sy * zoom + zoom / 2,
                                      cx * zoom + zoom / 2, cy * zoom + zoom / 2)
            | Rect ->
                let lx, hx = min sx cx, max sx cx
                let ly, hy = min sy cy, max sy cy
                g.DrawRectangle(pen, lx * zoom, ly * zoom, (hx - lx + 1) * zoom, (hy - ly + 1) * zoom)
            | _ -> ()

    member private _.OnCanvasMouseDown(e: MouseEventArgs) =
        if e.Button = MouseButtons.Left || e.Button = MouseButtons.Right then
            let (px, py) = toPixel e
            strokeValue <- if e.Button = MouseButtons.Right then VoidMat else materialValue material
            painting <- true
            dragStart <- (px, py)
            dragCur <- (px, py)
            lastPx <- (px, py)
            match tool with
            | Brush ->
                pushUndo ()
                stampBrush pixels px py brushRadius strokeValue
                refreshCanvas ()
            | Fill ->
                pushUndo ()
                floodFill pixels px py strokeValue
                painting <- false
                refreshCanvas ()
            | Line | Rect -> canvas.Invalidate()  // begin preview

    member private _.OnCanvasMouseMove(e: MouseEventArgs) =
        let (px, py) = toPixel e
        coordLabel.Text <- sprintf "x: %d  y: %d" px py
        if painting then
            match tool with
            | Brush ->
                let (lx, ly) = lastPx
                drawLine pixels lx ly px py brushRadius strokeValue
                lastPx <- (px, py)
                refreshCanvas ()
            | Line | Rect ->
                dragCur <- (px, py)
                canvas.Invalidate()
            | Fill -> ()

    member private _.OnCanvasMouseUp(e: MouseEventArgs) =
        if painting then
            let (px, py) = toPixel e
            let (sx, sy) = dragStart
            match tool with
            | Line ->
                pushUndo ()
                drawLine pixels sx sy px py brushRadius strokeValue
            | Rect ->
                pushUndo ()
                drawRect pixels sx sy px py strokeValue
            | _ -> ()
            painting <- false
            refreshCanvas ()

    // ── Keyboard shortcuts ──

    override _.OnKeyDown(e: KeyEventArgs) =
        match e.KeyCode with
        | Keys.B -> selectTool Brush
        | Keys.F -> selectTool Fill
        | Keys.L -> selectTool Line
        | Keys.R -> selectTool Rect
        | Keys.D1 -> selectMaterial MVoid
        | Keys.D2 -> selectMaterial MWall
        | Keys.D3 -> selectMaterial MWater
        | Keys.D4 -> selectMaterial MBase
        | Keys.OemOpenBrackets ->
            if brushRadius > 0 then brushRadius <- brushRadius - 1; updateInfo ()
        | Keys.OemCloseBrackets ->
            brushRadius <- min 20 (brushRadius + 1); updateInfo ()
        | Keys.Oemplus | Keys.Add -> setZoom (zoom + 1)
        | Keys.OemMinus | Keys.Subtract -> setZoom (zoom - 1)
        | Keys.G ->
            spawnBtn.Checked <- not spawnBtn.Checked   // CheckedChanged updates showSpawns + refreshes
        | _ -> ()
        base.OnKeyDown e

    override _.OnFormClosing(e: FormClosingEventArgs) =
        if not (confirmDiscard ()) then e.Cancel <- true
        base.OnFormClosing e

    override _.OnFormClosed(e: FormClosedEventArgs) =
        bmp.Dispose()
        base.OnFormClosed e

// ─── Headless image → .LEV converter (CLI) ─────────────────────────────────

let private cliUsage () =
    Console.WriteLine(
        "FsRocketEditor — image to .LEV converter\n\n\
         Usage:\n\
        \  FsRocketEditor <input-image> <output.LEV> [fit] [colours] [caveThreshold]\n\n\
         Arguments:\n\
        \  input-image    .png/.bmp/.jpg/.gif to convert\n\
        \  output.LEV     destination level file\n\
        \  fit            stretch (default) | fit | center\n\
        \  colours        solid (default) — everything becomes solid wall\n\
        \                 keep          — black=void, #0099FF=water, #30C060=base,\n\
        \                                 anything else = nearest solid wall colour\n\
        \  caveThreshold  (solid only) 0-255: pixels darker than this become void\n\
        \                 caves. Omit or use -1 to keep the whole image solid.\n\n\
         With no arguments the graphical editor launches instead.\n\n\
         Colours the game understands: void/sky = 0x00 (black), water = 0x27,\n\
         base/landing-pad = 0x5C-0x5F, everything else = solid wall.")

/// Convert an image to a .LEV file. Returns a process exit code.
let convertImageToLev (input: string) (output: string) (fitArg: string)
                      (colorArg: string) (thresholdArg: string) : int =
    if not (IO.File.Exists input) then
        Console.Error.WriteLine(sprintf "Input image not found: %s" input)
        2
    else
        let fit =
            match fitArg.ToLowerInvariant() with
            | "fit" -> Fit
            | "center" | "centre" -> Center
            | "stretch" | "" -> Stretch
            | other -> Console.WriteLine(sprintf "Unknown fit '%s', using stretch." other); Stretch
        let colors =
            match colorArg.ToLowerInvariant() with
            | "keep" | "preserve" | "colours" | "colors" -> PreserveColors
            | "solid" | "walls" | "" -> SolidWalls
            | other -> Console.WriteLine(sprintf "Unknown colours mode '%s', using solid." other); SolidWalls
        let threshold =
            match Int32.TryParse thresholdArg with
            | true, v -> v
            | _ -> -1
        try
            use src = new Bitmap(input)
            let w, h, sample = bitmapSampler src
            let pixels = importImage w h sample
                            { Fit = fit; Colors = colors; VoidThreshold = threshold
                              KeyTolerance = DefaultKeyTolerance }
            let name = IO.Path.GetFileNameWithoutExtension output
            saveLevel output { Pixels = pixels; Viewports = (0, 0, MapWidth - 1, MapHeight - 1)
                               SpawnPoints = [||]; Name = name }
            Console.WriteLine(
                sprintf "Wrote %s  (%dx%d image, fit=%A, colours=%A, caveThreshold=%d)"
                        output w h fit colors threshold)
            Console.WriteLine("Tip: open it in the editor to carve caves and place landing pads (bases).")
            0
        with ex ->
            Console.Error.WriteLine("Conversion failed: " + ex.Message)
            1

// ─── Entry point ─────────────────────────────────────────────────────────

[<STAThread; EntryPoint>]
let main argv =
    match argv with
    | [||] ->
        Application.EnableVisualStyles()
        Application.SetCompatibleTextRenderingDefault false
        Application.Run(new EditorForm())
        0
    | [| "-h" |] | [| "--help" |] | [| "/?" |] ->
        cliUsage (); 0
    | [| input; output |] -> convertImageToLev input output "stretch" "solid" "-1"
    | [| input; output; fit |] -> convertImageToLev input output fit "solid" "-1"
    | [| input; output; fit; colours |] -> convertImageToLev input output fit colours "-1"
    | [| input; output; fit; colours; threshold |] -> convertImageToLev input output fit colours threshold
    | _ -> cliUsage (); 2
