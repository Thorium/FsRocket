/// FsRocket Level Editor — WinForms UI shell.
/// Thin layer over FsRocketEditor.EditorCore (all real logic lives there and is
/// unit-tested separately). Paints the 320x400 map scaled by an integer zoom,
/// handles brush/fill/line/rect tools, image import and .LEV save/load, and
/// shows the flyability check and runs the cleanup from LevelCheck.
module FsRocketEditor.EditorForm

open System
open System.Collections.Generic
open System.Drawing
open System.Drawing.Imaging
open System.Runtime.InteropServices
open System.Windows.Forms
open FsRocket.Physics
open FsRocket.Terrain
open FsRocketEditor.EditorCore
open FsRocketEditor.LevelCheck

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

[<Literal>]
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

[<Literal>]
let private goodLevelsText =
    "GOOD LEVELS — room to fly\n\
     ─────────────────────────\n\
     A ship needs about 9 px (map pixels) of free space to turn. Narrower space\n\
     looks like a tunnel but can't be flown — and a player may have no digging\n\
     weapon. Check (C) colours what the ship can't use:\n\
       red      pocket — enclosed space the ship can't enter\n\
       yellow   slit — a dead end or corner too narrow to fly into\n\
       cyan     narrow gap — the only way between two open areas, too tight\n\
       magenta  small sealed cave — open space with no pad and no way in\n\
       orange   speck — a loose bit of wall a few pixels big\n\
       white    under a pad — no ship-sized route to the other pads\n\
     The ring at the cursor is the ship's turning room.\n\n\
     Clean up (Ctrl+K) fixes them: fills pockets, slits and small caves (copying\n\
     the nearby terrain's texture), widens the narrow gaps, tunnels to cut-off\n\
     pads and removes specks; optionally it also trims thin spikes. Pads, the\n\
     space just above them and the map's grey frame are never changed. Undo\n\
     (Ctrl+Z) takes the whole cleanup back.\n\n\
     Tips: prefer big terrain pieces over many small ones, keep thin outlined\n\
     frames solid (or open them wide), and give every pad free sky above it."

[<Literal>]
let private controlsText =
    "TOOLS         B Brush   F Fill   L Line   R Rectangle\n\
     MATERIALS     1 Void    2 Wall   3 Water  4 Base\n\
     BRUSH SIZE    [  smaller     ]  larger\n\
     ZOOM          -  out         +  in\n\
     SPAWNS        G  toggle spawn-point preview\n\
     CHECK         C  toggle the flyability check   Ctrl+K clean up\n\
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
                               "Center (native size, centred)" |] |> Array.map box)
    fitCombo.SelectedIndex <- 0

    let modeLabel = new Label(Text = "Colours:", Left = 12, Top = 52, Width = 100)
    let modeCombo = new ComboBox(Left = 130, Top = 49, Width = 228,
                                 DropDownStyle = ComboBoxStyle.DropDownList)
    modeCombo.Items.AddRange([| "Walls only — everything solid"
                                "Keep colours — black/blue/green = void/water/base" |] |> Array.map box)
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

// ─── Clean-up options dialog ───────────────────────────────────────────────

let private promptCleanup (owner: IWin32Window) : CleanupOptions option =
    use dlg = new Form(Text = "Clean up level", FormBorderStyle = FormBorderStyle.FixedDialog,
                       StartPosition = FormStartPosition.CenterParent,
                       MinimizeBox = false, MaximizeBox = false,
                       ClientSize = Size(400, 268))
    let info = new Label(Left = 12, Top = 10, Width = 376, Height = 34, ForeColor = Color.DimGray,
                         Text = "Makes the level flyable for a ship (about 9 px of turning room). "
                                + "Pads, the space above them and the map frame are never changed.")
    let box (text: string) top on = new CheckBox(Text = text, Left = 12, Top = top, Width = 376, Checked = on)
    let fillChk = box "Fill pockets, dead-end slits and small sealed caves" 50 true
    let widenChk = box "Widen too-narrow gaps between open areas" 76 true
    let connectChk = box "Tunnel to pads no ship-sized route reaches" 102 true
    let speckChk = box "Remove loose specks (20 px or less)" 128 true
    let spikeChk = box "Trim thin wall spikes up to" 154 false
    spikeChk.Width <- 200
    let spikeNud = new NumericUpDown(Left = 216, Top = 152, Width = 50, Minimum = 1m, Maximum = 8m,
                                     Value = 4m, Enabled = false)
    let spikeUnit = new Label(Text = "px wide (x2+1)", Left = 272, Top = 156, Width = 110)
    spikeChk.CheckedChanged.Add(fun _ -> spikeNud.Enabled <- spikeChk.Checked)
    let ok = new Button(Text = "Clean up", Left = 208, Top = 222, Width = 86, DialogResult = DialogResult.OK)
    let cancel = new Button(Text = "Cancel", Left = 302, Top = 222, Width = 86, DialogResult = DialogResult.Cancel)
    dlg.Controls.AddRange([| info :> Control; fillChk; widenChk; connectChk; speckChk
                             spikeChk; spikeNud; spikeUnit; ok; cancel |])
    dlg.AcceptButton <- ok
    dlg.CancelButton <- cancel
    if dlg.ShowDialog(owner) = DialogResult.OK then
        Some { FillDeadSpace = fillChk.Checked
               WidenNarrowLinks = widenChk.Checked
               ConnectPads = connectChk.Checked
               RemoveSpecks = speckChk.Checked
               SpikeRadius = (if spikeChk.Checked then int spikeNud.Value else 0) }
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
    let mutable showCheck = false

    // ── Interaction state ──
    let mutable painting = false
    let mutable strokeValue = WallMat
    let mutable lastPx = (0, 0)
    let mutable dragStart = (0, 0)
    let mutable dragCur = (0, 0)

    // ── Undo / redo (bounded snapshot stacks) ──
    let undoList = List<byte array>()
    let redoList = List<byte array>()
    let undoLimit = 40

    // ── Render cache ──
    let bmp = new Bitmap(MapWidth, MapHeight, PixelFormat.Format32bppArgb)
    let mutable bmpBuf : int array = Array.empty   // reused scratch buffer for bitmap upload
    let mutable bmpDirty = true
    let mutable cachedSpawns : SpawnPoint array = [||]
    // Check overlay: recomputed when the pixels changed and no stroke is running
    let checkBmp = new Bitmap(MapWidth, MapHeight, PixelFormat.Format32bppArgb)
    let mutable checkDirty = true
    let mutable lastReport : Report voption = ValueNone
    let mutable cursorPx = (-100, -100)

    // ── Controls ──
    let canvas = new CanvasPanel()
    let scroller = new Panel(Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(0x20,0x20,0x28))
    let statusStrip = new StatusStrip()
    let coordLabel = new ToolStripStatusLabel(Text = "x: -  y: -", AutoSize = false, Width = 120)
    let infoLabel = new ToolStripStatusLabel(Text = "", TextAlign = ContentAlignment.MiddleLeft)
    let toolStrip = new ToolStrip()
    let menuStrip = new MenuStrip()

    // Tool / material buttons kept for radio-style check management.
    let toolButtons = Dictionary<Tool, ToolStripButton>()
    let matButtons = Dictionary<Material, ToolStripButton>()
    let sizeCombo = new ToolStripComboBox()
    let zoomLabel = new ToolStripLabel(Text = "Zoom 2x")
    let spawnBtn = new ToolStripButton("Spawns (G)", CheckOnClick = true)
    let checkBtn = new ToolStripButton("Check (C)", CheckOnClick = true)
    let checkLabel = new ToolStripStatusLabel(Text = "", Spring = true, TextAlign = ContentAlignment.MiddleRight)

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
        checkDirty <- true
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

    /// Run the check and redraw its overlay (between strokes: it takes a moment).
    let updateCheck () =
        if showCheck && checkDirty && not painting then
            let r = check pixels
            lastReport <- ValueSome r
            let data = checkBmp.LockBits(Rectangle(0, 0, MapWidth, MapHeight),
                                         ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb)
            let strideInts = data.Stride / 4
            let buf = Array.zeroCreate<int> (strideInts * MapHeight)
            for y in 0 .. MapHeight - 1 do
                for x in 0 .. MapWidth - 1 do
                    match r.Problems[y * MapWidth + x] with
                    | ValueSome p -> buf[y * strideInts + x] <- problemArgb p
                    | ValueNone -> ()
            Marshal.Copy(buf, 0, data.Scan0, buf.Length)
            checkBmp.UnlockBits data
            checkLabel.Text <- reportText r
            checkDirty <- false

    let setShowCheck on =
        showCheck <- on
        if not on then checkLabel.Text <- ""
        checkDirty <- true
        canvas.Invalidate()

    /// Clean up the level (undoable) with the options the dialog picks.
    let cleanUpLevel () =
        match promptCleanup this with
        | None -> ()
        | Some opt ->
            Cursor.Current <- Cursors.WaitCursor
            let result = cleanup opt pixels
            Cursor.Current <- Cursors.Default
            if result.Pixels <> pixels then
                pushUndo ()
                pixels <- result.Pixels
                refreshCanvas ()
            let left = if result.CutOffPads > 0 then $"\n\n%d{result.CutOffPads} pads still have no ship-sized route." else ""
            MessageBox.Show(
                $"Filled %d{result.Filled} px, opened %d{result.Opened} px, %d{result.Tunnels} tunnels.%s{left}\n\nCtrl+Z undoes the cleanup.",
                "Clean up level", MessageBoxButtons.OK, MessageBoxIcon.Information) |> ignore

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
            zoomLabel.Text <- $"Zoom %d{zoom}x"
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

    let loadFromPath (path: string) =
        try
            let lv = loadLevel path
            pixels <- lv.Pixels
            viewports <- lv.Viewports
            filePath <- path
            levelName <- lv.Name
            dirty <- false
            undoList.Clear(); redoList.Clear()
            updateTitle (); refreshCanvas ()
        with ex ->
            MessageBox.Show("Could not load level:\n" + ex.Message, "Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error) |> ignore

    let openLevel () =
        if confirmDiscard () then
            use dlg = new OpenFileDialog(Filter = "AUTS level (*.LEV)|*.LEV|All files (*.*)|*.*")
            if dlg.ShowDialog(this) = DialogResult.OK then loadFromPath dlg.FileName

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
        // a level the ship can't fly everywhere asks first
        let r = check pixels
        let publish =
            not (hasProblems r)
            || MessageBox.Show(reportText r + "\n\nPublish anyway? (No = cancel, then Ctrl+K to clean up)",
                               "Publish to game", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) = DialogResult.Yes
        if not publish then () else
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
                        || MessageBox.Show($"%s{fileName} already exists in the game folder. Overwrite?",
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
        let b = new ToolStripButton(text, null, (fun _ _ -> selectTool t), CheckOnClick = false)
        toolButtons[t] <- b
        toolStrip.Items.Add b |> ignore

    let addMatButton (m: Material) (text: string) (argb: int) =
        let b =
            new ToolStripButton(text, makeSwatch argb, (fun _ _ -> selectMaterial m),
                CheckOnClick = false,
                ImageAlign = ContentAlignment.MiddleLeft,
                TextImageRelation = TextImageRelation.ImageBeforeText
            )
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
        let levelMenu = new ToolStripMenuItem("&Level")
        levelMenu.DropDownItems.AddRange([|
            mi "&Check flyability (C)" Keys.None (fun () -> checkBtn.Checked <- not checkBtn.Checked)
            mi "Clean &up..." (Keys.Control ||| Keys.K) cleanUpLevel
        |])
        let helpMenu = new ToolStripMenuItem("&Help")
        helpMenu.DropDownItems.AddRange([|
            mi "&Colour guide / instructions" Keys.F1
               (fun () -> MessageBox.Show(colorGuideText, "Colour guide", MessageBoxButtons.OK, MessageBoxIcon.Information) |> ignore)
            mi "&Good levels" Keys.None
               (fun () -> MessageBox.Show(goodLevelsText, "Good levels", MessageBoxButtons.OK, MessageBoxIcon.Information) |> ignore)
            mi "&Controls" Keys.None
               (fun () -> MessageBox.Show(controlsText, "Controls", MessageBoxButtons.OK, MessageBoxIcon.Information) |> ignore)
        |])
        menuStrip.Items.AddRange [| fileMenu :> ToolStripItem; editMenu :> ToolStripItem; levelMenu :> ToolStripItem; helpMenu :> ToolStripItem |]

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
        sizeCombo.Items.AddRange([| "1"; "3"; "5"; "7"; "9"; "15"; "25" |] |> Array.map box)
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
        checkBtn.CheckedChanged.Add(fun _ -> setShowCheck checkBtn.Checked)
        toolStrip.Items.Add checkBtn |> ignore

        // ── Status strip ──
        statusStrip.Items.AddRange [| coordLabel :> ToolStripItem; infoLabel :> ToolStripItem; checkLabel :> ToolStripItem |]

        // ── Canvas ──
        canvas.Size <- Size(MapWidth * zoom, MapHeight * zoom)
        canvas.Paint.Add this.OnCanvasPaint
        canvas.MouseDown.Add this.OnCanvasMouseDown
        canvas.MouseMove.Add this.OnCanvasMouseMove
        canvas.MouseUp.Add this.OnCanvasMouseUp
        canvas.MouseLeave.Add(fun _ ->
            coordLabel.Text <- "x: -  y: -"
            cursorPx <- (-100, -100)
            if showCheck then canvas.Invalidate())
        scroller.Controls.Add canvas

        // ── Form composition (z-order: Fill first, then bottom, then top bars) ──
        this.Controls.Add scroller
        this.Controls.Add statusStrip
        this.Controls.Add toolStrip
        this.Controls.Add menuStrip
        this.MainMenuStrip <- menuStrip

        this.Text <- "FsRocket Level Editor"
        this.ClientSize <- Size(960, 720)
        this.KeyPreview <- true
        this.BackColor <- Color.FromArgb(0x20, 0x20, 0x28)

        selectTool Brush
        selectMaterial MWall
        updateInfo ()
        updateTitle ()

    /// Open a level file (the editor's command-line argument).
    member _.LoadFile(path: string) = loadFromPath path

    // ── Canvas painting ──

    member private _.OnCanvasPaint(e: PaintEventArgs) =
        updateBitmap ()
        let g = e.Graphics
        g.InterpolationMode <- Drawing2D.InterpolationMode.NearestNeighbor
        g.PixelOffsetMode <- Drawing2D.PixelOffsetMode.Half
        g.DrawImage(bmp, Rectangle(0, 0, MapWidth * zoom, MapHeight * zoom))

        // Flyability check overlay and the ship's turning room at the cursor
        if showCheck then
            updateCheck ()
            g.DrawImage(checkBmp, Rectangle(0, 0, MapWidth * zoom, MapHeight * zoom))
            let (cx, cy) = cursorPx
            let r = float32 ((float ShipRadius + 0.5) * float zoom)
            let ccx, ccy = float32 (cx * zoom + zoom / 2), float32 (cy * zoom + zoom / 2)
            use ring = new Pen(Color.FromArgb(0xD0, 120, 255, 140), 1.5f)
            g.DrawEllipse(ring, ccx - r, ccy - r, 2.0f * r, 2.0f * r)

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
            | Brush | Fill -> ()

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
        coordLabel.Text <- $"x: %d{px}  y: %d{py}"
        if showCheck && cursorPx <> (px, py) then
            cursorPx <- (px, py)
            if not painting then canvas.Invalidate()
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
            | Brush | Fill -> ()
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
        | Keys.C when not e.Control ->
            checkBtn.Checked <- not checkBtn.Checked   // CheckedChanged shows / hides the check
        | Keys.G ->
            spawnBtn.Checked <- not spawnBtn.Checked   // CheckedChanged updates showSpawns + refreshes
        | _ -> ()
        base.OnKeyDown e

    override _.OnFormClosing(e: FormClosingEventArgs) =
        if not (confirmDiscard ()) then e.Cancel <- true
        base.OnFormClosing e

    override _.OnFormClosed(e: FormClosedEventArgs) =
        bmp.Dispose()
        checkBmp.Dispose()
        base.OnFormClosed e

// ─── Headless image → .LEV converter (CLI) ─────────────────────────────────

let private cliUsage () =
    [ "FsRocketEditor - image to .LEV converter"
      ""
      "Usage:"
      "  FsRocketEditor <input-image> <output.LEV> [fit] [colours] [caveThreshold]"
      ""
      "Arguments:"
      "  input-image    .png/.bmp/.jpg/.gif to convert"
      "  output.LEV     destination level file"
      "  fit            stretch (default) | fit | center"
      "  colours        solid (default) - everything becomes solid wall"
      "                 keep          - black=void, #0099FF=water, #30C060=base,"
      "                                 anything else = nearest solid wall colour"
      "  caveThreshold  (solid only) 0-255: pixels darker than this become void"
      "                 caves. Omit or use -1 to keep the whole image solid."
      ""
      "With no arguments the graphical editor launches instead; with just a"
      ".LEV file it opens that level."
      ""
      "Colours the game understands: void/sky = 0x00 (black), water = 0x27,"
      "base/landing-pad = 0x5C-0x5F, everything else = solid wall."
      ""
      EditorCli.usage ]
    |> String.concat "\n"
    |> Console.WriteLine

/// Convert an image to a .LEV file. Returns a process exit code.
let convertImageToLev (input: string) (output: string) (fitArg: string)
                      (colorArg: string) (thresholdArg: string) : int =
    if not (IO.File.Exists input) then
        Console.Error.WriteLine($"Input image not found: %s{input}")
        2
    else
        let fit =
            match fitArg.ToLowerInvariant() with
            | "fit" -> Fit
            | "center" | "centre" -> Center
            | "stretch" | "" -> Stretch
            | other -> Console.WriteLine($"Unknown fit '%s{other}', using stretch."); Stretch
        let colors =
            match colorArg.ToLowerInvariant() with
            | "keep" | "preserve" | "colours" | "colors" -> PreserveColors
            | "solid" | "walls" | "" -> SolidWalls
            | other -> Console.WriteLine($"Unknown colours mode '%s{other}', using solid."); SolidWalls
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

/// A GUI exe has no console: with arguments, print into the one it was
/// started from (piped output needs nothing).
[<DllImport("kernel32.dll")>]
extern bool private AttachConsole(int processId)

[<STAThread; EntryPoint>]
let main argv =
    if argv.Length > 0 && not Console.IsOutputRedirected then AttachConsole -1 |> ignore
    match argv with
    | [||] ->
        Application.EnableVisualStyles()
        Application.SetCompatibleTextRenderingDefault false
        Application.Run(new EditorForm())
        0
    | [| "-h" |] | [| "--help" |] | [| "/?" |] ->
        cliUsage (); 0
    | _ when argv[0].StartsWith "--" ->
        match EditorCli.tryRun argv with
        | Some code -> code
        | None -> cliUsage (); 2
    | [| level |] when level.EndsWith(".LEV", StringComparison.OrdinalIgnoreCase) ->
        Application.EnableVisualStyles()
        Application.SetCompatibleTextRenderingDefault false
        let form = new EditorForm()
        form.LoadFile level
        Application.Run form
        0
    | [| input; output |] -> convertImageToLev input output "stretch" "solid" "-1"
    | [| input; output; fit |] -> convertImageToLev input output fit "solid" "-1"
    | [| input; output; fit; colours |] -> convertImageToLev input output fit colours "-1"
    | [| input; output; fit; colours; threshold |] -> convertImageToLev input output fit colours threshold
    | _ -> cliUsage (); 2
