using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

[assembly: AssemblyTitle("PBI Lineage Studio")]
[assembly: AssemblyProduct("PBI Lineage Studio")]
[assembly: AssemblyDescription("Local Power BI semantic model lineage viewer")]

namespace PbiLineageStudio {
  static class Program {
    [STAThread]
    static void Main(string[] args) {
      UpdateDiagnostics.Write("Process started. Arguments: " + String.Join(" ", args));
      if (args.Length >= 3 && String.Equals(args[0], "--apply-update", StringComparison.OrdinalIgnoreCase)) {
        int parentProcessId;
        if (Int32.TryParse(args[2], out parentProcessId)) UpdateApplier.Apply(args[1], parentProcessId);
        UpdateDiagnostics.Write("Update helper finished.");
        return;
      }
      UpdateStorage.ScheduleOldFolderCleanup();
      Application.EnableVisualStyles();
      Application.SetCompatibleTextRenderingDefault(false);
      Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs eventArgs) {
        UpdateDiagnostics.Write("UI exception: " + eventArgs.Exception);
      };
      AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs eventArgs) {
        UpdateDiagnostics.Write("Unhandled exception: " + eventArgs.ExceptionObject);
      };
      var form = new MainForm();
      if (args.Length > 0 && Directory.Exists(args[0])) form.StartupFolder = args[0];
      form.Shown += delegate {
        UpdateDiagnostics.Write("Main window shown.");
        UpdateStorage.SchedulePreviousVersionCleanup();
      };
      form.FormClosed += delegate(object sender, FormClosedEventArgs eventArgs) {
        UpdateDiagnostics.Write("Main window closed. Reason: " + eventArgs.CloseReason);
      };
      Application.Run(form);
      UpdateDiagnostics.Write("Application message loop ended.");
    }
  }

  public static class UpdateDiagnostics {
    static readonly object Sync = new object();

    public static string LogPath {
      get {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PBI Lineage Studio", "update.log");
      }
    }

    public static void Write(string message) {
      try {
        lock (Sync) {
          var folder = Path.GetDirectoryName(LogPath);
          if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
          var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " | PID " + Process.GetCurrentProcess().Id +
            " | " + Assembly.GetExecutingAssembly().Location + " | " + message + Environment.NewLine;
          File.AppendAllText(LogPath, line, Encoding.UTF8);
        }
      } catch {
        // Diagnostics must never interrupt application startup or updating.
      }
    }
  }

  public class UpdateManifest {
    public string version { get; set; }
    public string downloadUrl { get; set; }
    public string sha256 { get; set; }
    public string releaseNotesUrl { get; set; }
  }

  public class UpdateWebClient : WebClient {
    public UpdateWebClient() {
      Headers[HttpRequestHeader.UserAgent] = "PBI-Lineage-Studio-Updater";
    }

    protected override WebRequest GetWebRequest(Uri address) {
      var request = base.GetWebRequest(address);
      if (request != null) request.Timeout = 15000;
      return request;
    }
  }

  public static class UpdateStorage {
    const string ProductFolder = "PBI Lineage Studio";
    const string UpdatesFolder = "Updates";

    public static string RootPath {
      get {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolder, UpdatesFolder);
      }
    }

    public static string CreateFolder(string version) {
      var safeVersion = Regex.Replace(version ?? "update", "[^0-9A-Za-z.-]", "-");
      var root = RootPath;
      Directory.CreateDirectory(root);
      var folder = Path.Combine(root, safeVersion + "-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(folder);
      return folder;
    }

    public static void ScheduleOldFolderCleanup() {
      ThreadPool.QueueUserWorkItem(delegate {
        try {
          var root = RootPath;
          if (!Directory.Exists(root)) return;
          foreach (var folder in Directory.GetDirectories(root)) {
            try {
              if (Directory.GetLastWriteTimeUtc(folder) < DateTime.UtcNow.AddDays(-7)) TryDeleteManagedFolder(folder);
            } catch { }
          }
        } catch { }
      });
    }

    public static void SchedulePreviousVersionCleanup() {
      ThreadPool.QueueUserWorkItem(delegate {
        try {
          Thread.Sleep(5000);
          var previousPath = Assembly.GetExecutingAssembly().Location + ".previous";
          if (File.Exists(previousPath)) File.Delete(previousPath);
        } catch {
          // A later successful launch retries cleanup if another process temporarily holds the backup.
        }
      });
    }

    public static void TryDeleteManagedFolder(string folder) {
      try {
        if (!IsManagedFolder(folder) || !Directory.Exists(folder)) return;
        Directory.Delete(folder, true);
      } catch {
        // A running helper can still hold the folder; a later launch retries old-folder cleanup.
      }
    }

    static bool IsManagedFolder(string folder) {
      if (String.IsNullOrWhiteSpace(folder)) return false;
      var root = Path.GetFullPath(RootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
      var candidate = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
      var executable = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
      return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
        !candidate.Equals(root, StringComparison.OrdinalIgnoreCase) &&
        !executable.StartsWith(candidate, StringComparison.OrdinalIgnoreCase);
    }
  }

  public static class UpdateApplier {
    public static void Apply(string targetPath, int parentProcessId) {
      var stage = "waiting for the running application to close";
      try {
        UpdateDiagnostics.Write("Update helper waiting for PID " + parentProcessId + ". Target: " + targetPath);
        WaitForProcessExit(parentProcessId);
        var sourcePath = Assembly.GetExecutingAssembly().Location;
        var incomingPath = targetPath + ".incoming";
        var backupPath = targetPath + ".previous";
        var targetDirectory = Path.GetDirectoryName(targetPath);

        if (String.IsNullOrEmpty(targetDirectory) || !Directory.Exists(targetDirectory)) throw new DirectoryNotFoundException("The application folder no longer exists.");
        stage = "preparing the replacement file";
        if (File.Exists(incomingPath)) File.Delete(incomingPath);
        File.Copy(sourcePath, incomingPath, true);

        stage = "replacing the installed application";
        if (File.Exists(targetPath)) {
          if (File.Exists(backupPath)) File.Delete(backupPath);
          File.Replace(incomingPath, targetPath, backupPath, true);
        } else {
          File.Move(incomingPath, targetPath);
        }

        stage = "restarting the updated application";
        var updatedProcess = Process.Start(new ProcessStartInfo {
          FileName = targetPath,
          WorkingDirectory = targetDirectory,
          UseShellExecute = true
        });
        if (updatedProcess == null) throw new InvalidOperationException("The updated application could not be started.");
        UpdateDiagnostics.Write("Updated application started as PID " + updatedProcess.Id + ". Keeping helper alive until it closes.");
        stage = "monitoring the updated application";
        updatedProcess.WaitForExit();
        UpdateDiagnostics.Write("Updated application PID " + updatedProcess.Id + " exited with code " + updatedProcess.ExitCode + ".");
      } catch (Exception ex) {
        UpdateDiagnostics.Write("Update failed during " + stage + ": " + ex);
        MessageBox.Show("The update could not be installed." + Environment.NewLine + Environment.NewLine +
          "Step: " + stage + Environment.NewLine +
          "Target: " + targetPath + Environment.NewLine + Environment.NewLine + ex.Message,
          "PBI Lineage Studio Update", MessageBoxButtons.OK, MessageBoxIcon.Error);
      }
    }

    static void WaitForProcessExit(int processId) {
      try {
        using (var process = Process.GetProcessById(processId)) {
          if (!process.WaitForExit(30000)) throw new TimeoutException("PBI Lineage Studio did not close in time.");
        }
      } catch (ArgumentException) {
        // The original process has already exited.
      }
    }

    static string QuoteArgument(string value) {
      return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
    }
  }

  public static class Theme {
    public static readonly Color Window = Color.FromArgb(244, 247, 251);
    public static readonly Color Surface = Color.White;
    public static readonly Color SurfaceMuted = Color.FromArgb(248, 250, 252);
    public static readonly Color Border = Color.FromArgb(226, 232, 240);
    public static readonly Color Ink = Color.FromArgb(15, 23, 42);
    public static readonly Color Muted = Color.FromArgb(100, 116, 139);
    public static readonly Color Primary = Color.FromArgb(79, 70, 229);
    public static readonly Color PrimaryHover = Color.FromArgb(67, 56, 202);
    public static readonly Color PrimarySoft = Color.FromArgb(238, 242, 255);
    public static readonly Color Cyan = Color.FromArgb(8, 145, 178);
    public static readonly Color Green = Color.FromArgb(5, 150, 105);
    public static readonly Color Purple = Color.FromArgb(124, 58, 237);
    public static readonly Color Amber = Color.FromArgb(217, 119, 6);

    public static System.Drawing.Drawing2D.GraphicsPath RoundRect(RectangleF rect, float radius) {
      var path = new System.Drawing.Drawing2D.GraphicsPath();
      var diameter = Math.Max(2F, radius * 2F);
      path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
      path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
      path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
      path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
      path.CloseFigure();
      return path;
    }
  }

  public class StudioMenuColorTable : ProfessionalColorTable {
    static readonly Color MenuHover = Color.FromArgb(245, 242, 237);
    static readonly Color MenuPressed = Color.FromArgb(238, 233, 225);
    static readonly Color MenuOutline = Color.FromArgb(218, 211, 201);

    public override Color MenuItemSelected { get { return MenuHover; } }
    public override Color MenuItemBorder { get { return MenuOutline; } }
    public override Color MenuItemSelectedGradientBegin { get { return MenuHover; } }
    public override Color MenuItemSelectedGradientEnd { get { return MenuHover; } }
    public override Color MenuItemPressedGradientBegin { get { return MenuPressed; } }
    public override Color MenuItemPressedGradientMiddle { get { return MenuPressed; } }
    public override Color MenuItemPressedGradientEnd { get { return MenuPressed; } }
    public override Color ToolStripDropDownBackground { get { return Theme.Surface; } }
    public override Color ImageMarginGradientBegin { get { return Theme.Surface; } }
    public override Color ImageMarginGradientMiddle { get { return Theme.Surface; } }
    public override Color ImageMarginGradientEnd { get { return Theme.Surface; } }
    public override Color SeparatorDark { get { return Theme.Border; } }
    public override Color SeparatorLight { get { return Theme.Surface; } }
  }

  public class PremiumButton : Button {
    bool hovering;
    public int CornerRadius = 8;

    public PremiumButton() {
      FlatStyle = FlatStyle.Flat;
      FlatAppearance.BorderSize = 0;
      BackColor = Theme.Surface;
      ForeColor = Theme.Ink;
      Font = new Font("Segoe UI", 9F, FontStyle.Bold);
      Cursor = Cursors.Hand;
      SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnMouseEnter(EventArgs e) { hovering = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovering = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e) {
      e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
      e.Graphics.Clear(Parent == null ? Theme.Window : Parent.BackColor);
      var rect = new RectangleF(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
      var fill = hovering && Enabled ? HoverColor(BackColor) : BackColor;
      using (var path = Theme.RoundRect(rect, CornerRadius))
      using (var brush = new SolidBrush(Enabled ? fill : Theme.SurfaceMuted))
      using (var border = new Pen(hovering && Enabled ? Theme.Border : BackColor == Theme.Surface || BackColor == Theme.SurfaceMuted ? Theme.Border : fill, 1F)) {
        e.Graphics.FillPath(brush, path);
        e.Graphics.DrawPath(border, path);
      }
      TextRenderer.DrawText(e.Graphics, Text, Font, Rectangle.Round(rect), Enabled ? hovering ? Theme.Ink : ForeColor : Theme.Muted,
        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }

    Color HoverColor(Color color) {
      return Color.FromArgb(241, 245, 249);
    }
  }

  public enum ChevronDirection { Left, Right, Up, Down }

  public class ChevronButton : PremiumButton {
    ChevronDirection direction;

    public ChevronDirection Direction {
      get { return direction; }
      set { direction = value; Invalidate(); }
    }

    public ChevronButton(ChevronDirection initialDirection) {
      direction = initialDirection;
      Text = "";
    }

    protected override void OnPaint(PaintEventArgs e) {
      base.OnPaint(e);
      e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
      var dx = direction == ChevronDirection.Right ? 1F : direction == ChevronDirection.Left ? -1F : 0F;
      var dy = direction == ChevronDirection.Down ? 1F : direction == ChevronDirection.Up ? -1F : 0F;
      var px = -dy;
      var py = dx;
      var cx = ClientSize.Width / 2F;
      var cy = ClientSize.Height / 2F;
      using (var pen = new Pen(Enabled ? ForeColor : Theme.Muted, 1.8F)) {
        pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
        pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
        pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
        foreach (var offset in new float[] { -4F, 5F }) {
          var tip = new PointF(cx + dx * offset, cy + dy * offset);
          var back = new PointF(tip.X - dx * 5F, tip.Y - dy * 5F);
          e.Graphics.DrawLines(pen, new PointF[] {
            new PointF(back.X + px * 4F, back.Y + py * 4F),
            tip,
            new PointF(back.X - px * 4F, back.Y - py * 4F)
          });
        }
      }
    }
  }

  public class VerticalTextLabel : Control {
    public VerticalTextLabel() {
      SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaint(PaintEventArgs e) {
      e.Graphics.Clear(BackColor);
      e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
      e.Graphics.TranslateTransform(0, ClientSize.Height);
      e.Graphics.RotateTransform(-90F);
      using (var brush = new SolidBrush(ForeColor))
      using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }) {
        e.Graphics.DrawString(Text, Font, brush, new RectangleF(0, 0, ClientSize.Height, ClientSize.Width), format);
      }
    }
  }

  public class CueTextBox : TextBox {
    public string Cue = "";
    const int EM_SETCUEBANNER = 0x1501;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    public CueTextBox() {
      BorderStyle = BorderStyle.FixedSingle;
      Font = new Font("Segoe UI", 9.5F);
      ForeColor = Theme.Ink;
      BackColor = Theme.Surface;
    }

    protected override void OnHandleCreated(EventArgs e) {
      base.OnHandleCreated(e);
      if (Cue.Length > 0) SendMessage(Handle, EM_SETCUEBANNER, (IntPtr)1, Cue);
    }
  }

  public class LogoMark : Control {
    readonly Bitmap mark = Branding.CreateLogoBitmap(38, Theme.Surface);

    public LogoMark() {
      BackColor = Theme.Surface;
      SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaint(PaintEventArgs e) {
      e.Graphics.Clear(BackColor);
      var x = Math.Max(0, (ClientSize.Width - mark.Width) / 2);
      var y = Math.Max(0, (ClientSize.Height - mark.Height) / 2);
      e.Graphics.DrawImageUnscaled(mark, x, y);
    }

    protected override void Dispose(bool disposing) {
      if (disposing) mark.Dispose();
      base.Dispose(disposing);
    }
  }

  public class ExportOptionsDialog : Form {
    readonly CheckBox includeDetails = new CheckBox();
    public bool IncludeDetails { get { return includeDetails.Checked && includeDetails.Enabled; } }

    public ExportOptionsDialog(string selectedAsset) {
      Text = "Export Data Flow to PNG";
      Icon = Branding.CreateAppIcon();
      Width = 470;
      Height = 238;
      MinimumSize = MaximumSize = new Size(470, 238);
      FormBorderStyle = FormBorderStyle.FixedDialog;
      StartPosition = FormStartPosition.CenterParent;
      MaximizeBox = false;
      MinimizeBox = false;
      ShowInTaskbar = false;
      BackColor = Theme.Surface;
      Font = new Font("Segoe UI", 9F);

      var title = new Label();
      title.Text = "Export the complete Data Flow";
      title.Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold);
      title.ForeColor = Theme.Ink;
      title.AutoSize = true;
      title.Location = new Point(24, 22);

      var description = new Label();
      description.Text = "The PNG will include every node on the logical canvas, including content outside the current viewport.";
      description.ForeColor = Theme.Muted;
      description.Location = new Point(26, 55);
      description.Size = new Size(410, 38);

      includeDetails.Text = "Include selected asset details on the right";
      includeDetails.Location = new Point(28, 104);
      includeDetails.Size = new Size(390, 24);
      includeDetails.ForeColor = Theme.Ink;
      includeDetails.Enabled = !String.IsNullOrEmpty(selectedAsset);
      includeDetails.Checked = includeDetails.Enabled;

      var selection = new Label();
      selection.Text = includeDetails.Enabled ? "Selected: " + selectedAsset : "Select an asset to include Code Inspector details.";
      selection.ForeColor = Theme.Muted;
      selection.Location = new Point(48, 130);
      selection.Size = new Size(380, 22);

      var cancel = new PremiumButton();
      cancel.Text = "Cancel";
      cancel.Size = new Size(92, 36);
      cancel.Location = new Point(244, 162);
      cancel.DialogResult = DialogResult.Cancel;

      var export = new PremiumButton();
      export.Text = "Export PNG";
      export.Size = new Size(104, 36);
      export.Location = new Point(342, 162);
      export.BackColor = Theme.Primary;
      export.ForeColor = Color.White;
      export.DialogResult = DialogResult.OK;

      Controls.Add(title);
      Controls.Add(description);
      Controls.Add(includeDetails);
      Controls.Add(selection);
      Controls.Add(cancel);
      Controls.Add(export);
      AcceptButton = export;
      CancelButton = cancel;
    }
  }

  public class MainForm : Form {
    const string UpdateManifestUrl = "https://github.com/rohitbaviskar/pbi-lineage-studio/releases/latest/download/latest.json";
    readonly CueTextBox folderText = new CueTextBox();
    readonly Button browseButton = new PremiumButton();
    readonly Button loadButton = new PremiumButton();
    readonly CueTextBox searchText = new CueTextBox();
    readonly Button dataFlowButton = new PremiumButton();
    readonly Button dataModelButton = new PremiumButton();
    readonly Button zoomOutButton = new PremiumButton();
    readonly Button zoomInButton = new PremiumButton();
    readonly Button exportButton = new PremiumButton();
    readonly FlowLayoutPanel flowFilterBar = new FlowLayoutPanel();
    readonly FlowLayoutPanel canvasActionBar = new FlowLayoutPanel();
    readonly Button flowAllButton = new PremiumButton();
    readonly Button flowSourcesButton = new PremiumButton();
    readonly Button flowTablesButton = new PremiumButton();
    readonly Button flowColumnsButton = new PremiumButton();
    readonly Button flowMeasuresButton = new PremiumButton();
    readonly Button flowPagesButton = new PremiumButton();
    readonly ChevronButton detailsCollapseButton = new ChevronButton(ChevronDirection.Up);
    readonly ChevronButton sidebarCollapseButton = new ChevronButton(ChevronDirection.Right);
    readonly ChevronButton sidebarExpandButton = new ChevronButton(ChevronDirection.Left);
    readonly Label status = new Label();
    readonly TableBrowser tableBrowser = new TableBrowser();
    readonly LineageCanvas canvas = new LineageCanvas();
    readonly RichTextBox details = new RichTextBox();
    readonly SplitContainer body = new SplitContainer();
    readonly SplitContainer right = new SplitContainer();
    readonly FlowLayoutPanel modelTabs = new FlowLayoutPanel();
    readonly TableLayoutPanel canvasHost = new TableLayoutPanel();
    readonly TableLayoutPanel detailsHost = new TableLayoutPanel();
    readonly TableLayoutPanel sidebarHost = new TableLayoutPanel();
    readonly Panel flowToolbarHost = new Panel();
    readonly Panel sidebarHeader = new Panel();
    readonly Panel sidebarCollapsedRail = new Panel();
    readonly VerticalTextLabel sidebarRailLabel = new VerticalTextLabel();
    readonly Label sidebarHeaderLabel = new Label();
    readonly ToolTip paneToolTip = new ToolTip();
    readonly ToolStripMenuItem updateMenuItem = new ToolStripMenuItem();
    int layoutTabCount = 1;
    int previousDetailsHeight = 230;
    int previousSidebarWidth = 240;
    bool detailsCollapsed;
    bool sidebarCollapsed;
    bool updateInstalling;
    UpdateManifest availableUpdate;
    Graph graph = new Graph();
    public string StartupFolder;

    public MainForm() {
      Text = "PBI Lineage Studio - Power BI";
      Icon = Branding.CreateAppIcon();
      Width = 1440;
      Height = 900;
      MinimumSize = new Size(1060, 680);
      Font = new Font("Segoe UI", 9F);
      BackColor = Theme.Window;

      var shell = new TableLayoutPanel();
      shell.Dock = DockStyle.Fill;
      shell.ColumnCount = 1;
      shell.RowCount = 3;
      shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
      shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
      shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
      shell.BackColor = Theme.Window;
      Controls.Add(shell);

      var menuStrip = new MenuStrip();
      menuStrip.Dock = DockStyle.Fill;
      menuStrip.BackColor = Theme.Surface;
      menuStrip.ForeColor = Theme.Ink;
      menuStrip.Padding = new Padding(12, 1, 0, 1);
      menuStrip.Renderer = new ToolStripProfessionalRenderer(new StudioMenuColorTable());
      var fileMenu = new ToolStripMenuItem("&File");
      var openMenuItem = new ToolStripMenuItem("&Open model folder...");
      openMenuItem.ShortcutKeys = Keys.Control | Keys.O;
      openMenuItem.Click += delegate { BrowseFolder(); };
      var exportMenuItem = new ToolStripMenuItem("&Export PNG...");
      exportMenuItem.ShortcutKeys = Keys.Control | Keys.E;
      exportMenuItem.Click += delegate { ExportPng(); };
      var exitMenuItem = new ToolStripMenuItem("E&xit");
      exitMenuItem.Click += delegate { Close(); };
      fileMenu.DropDownItems.Add(openMenuItem);
      fileMenu.DropDownItems.Add(exportMenuItem);
      fileMenu.DropDownItems.Add(new ToolStripSeparator());
      fileMenu.DropDownItems.Add(exitMenuItem);
      var aboutMenu = new ToolStripMenuItem("&About");
      var aboutMenuItem = new ToolStripMenuItem("About PBI Lineage Studio");
      aboutMenuItem.Click += delegate { ShowAbout(); };
      aboutMenu.DropDownItems.Add(aboutMenuItem);
      updateMenuItem.Alignment = ToolStripItemAlignment.Right;
      updateMenuItem.Text = "Update available";
      updateMenuItem.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
      updateMenuItem.ForeColor = Theme.Amber;
      updateMenuItem.Visible = false;
      updateMenuItem.Click += delegate { InstallAvailableUpdateAsync(); };
      menuStrip.Items.Add(fileMenu);
      menuStrip.Items.Add(aboutMenu);
      menuStrip.Items.Add(updateMenuItem);
      MainMenuStrip = menuStrip;
      shell.Controls.Add(menuStrip, 0, 0);

      var top = new TableLayoutPanel();
      top.Dock = DockStyle.Fill;
      top.ColumnCount = 3;
      top.RowCount = 1;
      top.Padding = new Padding(20, 9, 20, 8);
      top.BackColor = Theme.SurfaceMuted;
      top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
      top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
      top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
      top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

      folderText.Dock = DockStyle.Fill;
      folderText.Margin = new Padding(0, 6, 8, 6);
      folderText.Cue = "Paste a .SemanticModel, definition, or tables folder path";
      browseButton.Text = "Browse";
      browseButton.Dock = DockStyle.Fill;
      browseButton.Margin = new Padding(4, 3, 4, 3);
      loadButton.Text = "Load model";
      loadButton.Dock = DockStyle.Fill;
      loadButton.Margin = new Padding(4, 3, 4, 3);
      loadButton.BackColor = Theme.Primary;
      loadButton.ForeColor = Color.White;
      dataFlowButton.Text = "Data flow";
      dataFlowButton.Size = new Size(130, 30);
      dataFlowButton.Margin = new Padding(4, 3, 2, 3);
      dataModelButton.Text = "Data model";
      dataModelButton.Size = new Size(138, 30);
      dataModelButton.Margin = new Padding(2, 3, 8, 3);
      zoomOutButton.Text = "-";
      zoomOutButton.Size = new Size(42, 30);
      zoomOutButton.Margin = new Padding(2, 3, 2, 3);
      zoomInButton.Text = "+";
      zoomInButton.Size = new Size(42, 30);
      zoomInButton.Margin = new Padding(2, 3, 6, 3);
      exportButton.Text = "Export PNG";
      exportButton.Size = new Size(112, 30);
      exportButton.Margin = new Padding(2, 3, 0, 3);
      exportButton.BackColor = Theme.Surface;
      exportButton.ForeColor = Theme.Primary;
      canvasActionBar.Dock = DockStyle.Right;
      canvasActionBar.FlowDirection = FlowDirection.LeftToRight;
      canvasActionBar.WrapContents = false;
      canvasActionBar.AutoSize = true;
      canvasActionBar.BackColor = Theme.Surface;
      canvasActionBar.Controls.Add(dataFlowButton);
      canvasActionBar.Controls.Add(dataModelButton);
      canvasActionBar.Controls.Add(zoomOutButton);
      canvasActionBar.Controls.Add(zoomInButton);
      canvasActionBar.Controls.Add(exportButton);
      flowFilterBar.Dock = DockStyle.None;
      flowFilterBar.FlowDirection = FlowDirection.LeftToRight;
      flowFilterBar.WrapContents = false;
      flowFilterBar.Padding = new Padding(8, 7, 8, 7);
      flowFilterBar.BackColor = Theme.Surface;
      flowFilterBar.AutoSize = true;
      ConfigureFilterButton(flowAllButton, "All");
      ConfigureFilterButton(flowSourcesButton, "Sources");
      ConfigureFilterButton(flowTablesButton, "Tables");
      ConfigureFilterButton(flowColumnsButton, "Columns");
      ConfigureFilterButton(flowMeasuresButton, "Measures");
      ConfigureFilterButton(flowPagesButton, "Pages");
      flowPagesButton.Enabled = false;
      flowFilterBar.Controls.Add(flowAllButton);
      flowFilterBar.Controls.Add(flowSourcesButton);
      flowFilterBar.Controls.Add(flowTablesButton);
      flowFilterBar.Controls.Add(flowColumnsButton);
      flowFilterBar.Controls.Add(flowMeasuresButton);
      flowFilterBar.Controls.Add(flowPagesButton);
      status.Text = "Ready  /  Choose a local semantic model to begin. Nothing leaves this computer.";

      top.Controls.Add(folderText, 0, 0);
      top.Controls.Add(browseButton, 1, 0);
      top.Controls.Add(loadButton, 2, 0);
      shell.Controls.Add(top, 0, 1);

      body.Dock = DockStyle.Fill;
      body.BackColor = Theme.Border;
      body.SplitterWidth = 1;
      body.Panel1.BackColor = Theme.Window;
      body.Panel2.BackColor = Theme.Surface;
      shell.Controls.Add(body, 0, 2);

      tableBrowser.Dock = DockStyle.Fill;
      tableBrowser.BackColor = Theme.Surface;
      body.Panel2.Controls.Add(tableBrowser);

      right.Dock = DockStyle.Fill;
      right.Orientation = Orientation.Horizontal;
      right.BackColor = Theme.Border;
      right.SplitterWidth = 1;
      right.Panel1.BackColor = Theme.Surface;
      right.Panel2.BackColor = Theme.Window;
      body.Panel1.Controls.Add(right);

      detailsHost.Dock = DockStyle.Fill;
      detailsHost.ColumnCount = 1;
      detailsHost.RowCount = 2;
      detailsHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
      detailsHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
      var detailsHeader = new Panel();
      detailsHeader.Dock = DockStyle.Fill;
      detailsHeader.BackColor = Theme.Surface;
      var detailsHeaderLabel = new Label();
      detailsHeaderLabel.Text = "CODE INSPECTOR";
      detailsHeaderLabel.Dock = DockStyle.Fill;
      detailsHeaderLabel.TextAlign = ContentAlignment.MiddleLeft;
      detailsHeaderLabel.Padding = new Padding(16, 0, 0, 0);
      detailsHeaderLabel.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
      detailsHeaderLabel.ForeColor = Theme.Muted;
      detailsCollapseButton.Dock = DockStyle.Right;
      detailsCollapseButton.Width = 52;
      detailsCollapseButton.Margin = new Padding(0, 5, 10, 5);
      detailsCollapseButton.BackColor = Theme.Surface;
      detailsCollapseButton.ForeColor = Theme.Muted;
      detailsHeader.Controls.Add(detailsHeaderLabel);
      detailsHeader.Controls.Add(detailsCollapseButton);
      details.Dock = DockStyle.Fill;
      details.ReadOnly = true;
      details.BorderStyle = BorderStyle.None;
      details.Font = new Font("Consolas", 9.5F);
      details.ForeColor = Color.FromArgb(51, 65, 85);
      details.WordWrap = false;
      details.BackColor = Theme.SurfaceMuted;
      details.Margin = new Padding(0);
      details.DetectUrls = false;
      detailsHost.Controls.Add(detailsHeader, 0, 0);
      detailsHost.Controls.Add(details, 0, 1);
      right.Panel1.Controls.Add(detailsHost);

      canvasHost.Dock = DockStyle.Fill;
      canvasHost.ColumnCount = 1;
      canvasHost.RowCount = 3;
      canvasHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
      canvasHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
      canvasHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
      canvas.Dock = DockStyle.Fill;
      flowToolbarHost.Dock = DockStyle.Fill;
      flowToolbarHost.BackColor = Theme.Surface;
      flowToolbarHost.Padding = new Padding(14, 9, 14, 7);
      flowFilterBar.Dock = DockStyle.Left;
      flowFilterBar.Margin = new Padding(0);
      flowToolbarHost.Controls.Add(flowFilterBar);
      flowToolbarHost.Controls.Add(canvasActionBar);
      canvasActionBar.BringToFront();
      modelTabs.Dock = DockStyle.Fill;
      modelTabs.FlowDirection = FlowDirection.LeftToRight;
      modelTabs.WrapContents = false;
      modelTabs.BackColor = Theme.Surface;
      modelTabs.Padding = new Padding(12, 4, 0, 2);
      canvasHost.Controls.Add(flowToolbarHost, 0, 0);
      canvasHost.Controls.Add(canvas, 0, 1);
      canvasHost.Controls.Add(modelTabs, 0, 2);
      right.Panel2.Controls.Add(canvasHost);
      BuildModelTabs();

      sidebarHost.Dock = DockStyle.Fill;
      sidebarHost.ColumnCount = 1;
      sidebarHost.RowCount = 3;
      sidebarHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
      sidebarHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
      sidebarHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
      sidebarHeader.Dock = DockStyle.Fill;
      sidebarHeader.BackColor = Theme.Surface;
      sidebarHeaderLabel.Text = "MODEL EXPLORER";
      sidebarHeaderLabel.Dock = DockStyle.Fill;
      sidebarHeaderLabel.TextAlign = ContentAlignment.MiddleLeft;
      sidebarHeaderLabel.Padding = new Padding(16, 0, 0, 0);
      sidebarHeaderLabel.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
      sidebarHeaderLabel.ForeColor = Theme.Muted;
      sidebarCollapseButton.Dock = DockStyle.Right;
      sidebarCollapseButton.Width = 52;
      sidebarCollapseButton.Margin = new Padding(0, 6, 10, 6);
      sidebarCollapseButton.BackColor = Theme.Surface;
      sidebarCollapseButton.ForeColor = Theme.Muted;
      sidebarHeader.Controls.Add(sidebarCollapseButton);
      sidebarHeader.Controls.Add(sidebarHeaderLabel);
      sidebarCollapseButton.BringToFront();

      var searchHost = new Panel();
      searchHost.Dock = DockStyle.Fill;
      searchHost.BackColor = Theme.Surface;
      searchHost.Padding = new Padding(14, 8, 14, 10);
      searchText.Dock = DockStyle.Fill;
      searchText.Cue = "Search tables, columns, measures...";
      searchText.Margin = new Padding(0);
      searchHost.Controls.Add(searchText);
      sidebarHost.Controls.Add(sidebarHeader, 0, 0);
      sidebarHost.Controls.Add(searchHost, 0, 1);
      sidebarCollapsedRail.Dock = DockStyle.Fill;
      sidebarCollapsedRail.BackColor = Theme.Surface;
      sidebarCollapsedRail.Visible = false;
      sidebarExpandButton.Dock = DockStyle.Top;
      sidebarExpandButton.Height = 46;
      sidebarExpandButton.Margin = new Padding(6);
      sidebarExpandButton.BackColor = Theme.Surface;
      sidebarExpandButton.ForeColor = Theme.Muted;
      sidebarRailLabel.Dock = DockStyle.Fill;
      sidebarRailLabel.Text = "MODEL EXPLORER";
      sidebarRailLabel.Font = new Font("Segoe UI", 8F, FontStyle.Bold);
      sidebarRailLabel.ForeColor = Theme.Muted;
      sidebarRailLabel.BackColor = Theme.Surface;
      sidebarRailLabel.Cursor = Cursors.Hand;
      sidebarCollapsedRail.Controls.Add(sidebarRailLabel);
      sidebarCollapsedRail.Controls.Add(sidebarExpandButton);
      sidebarExpandButton.BringToFront();
      body.Panel2.Controls.Clear();
      sidebarHost.Controls.Add(tableBrowser, 0, 2);
      body.Panel2.Controls.Add(sidebarHost);
      body.Panel2.Controls.Add(sidebarCollapsedRail);

      paneToolTip.InitialDelay = 350;
      paneToolTip.SetToolTip(detailsCollapseButton, "Collapse Code Inspector");
      paneToolTip.SetToolTip(sidebarCollapseButton, "Collapse Model Explorer");
      paneToolTip.SetToolTip(sidebarExpandButton, "Expand Model Explorer");
      paneToolTip.SetToolTip(sidebarRailLabel, "Expand Model Explorer");

      browseButton.Click += delegate { BrowseFolder(); };
      loadButton.Click += delegate { LoadFolder(folderText.Text); };
      searchText.TextChanged += delegate { PopulateList(); };
      dataFlowButton.Click += delegate { SetViewMode("dataflow"); };
      dataModelButton.Click += delegate { SetViewMode("datamodel"); };
      zoomOutButton.Click += delegate { canvas.ZoomOut(); };
      zoomInButton.Click += delegate { canvas.ZoomIn(); };
      exportButton.Click += delegate { ExportPng(); };
      flowAllButton.Click += delegate { SetFlowFilter("all"); };
      flowSourcesButton.Click += delegate { ToggleFlowFilter("source"); };
      flowTablesButton.Click += delegate { ToggleFlowFilter("table"); };
      flowColumnsButton.Click += delegate { ToggleFlowFilter("column"); };
      flowMeasuresButton.Click += delegate { ToggleFlowFilter("measure"); };
      flowPagesButton.Click += delegate { ToggleFlowFilter("page"); };
      detailsCollapseButton.Click += delegate { ToggleDetailsPane(); };
      sidebarCollapseButton.Click += delegate { ToggleSidebarPane(); };
      sidebarExpandButton.Click += delegate { ToggleSidebarPane(); };
      sidebarRailLabel.Click += delegate { ToggleSidebarPane(); };
      tableBrowser.SelectionChanged += delegate(string id) { SelectObjectById(id); };
      canvas.SelectionChanged += delegate(string id) { SelectObjectById(id); };
      canvas.RelationshipSelectionChanged += delegate(Edge edge) { SelectRelationship(edge); };
      Resize += delegate { EnsureTableListVisible(); };
      Shown += delegate {
        SetViewMode("dataflow");
        EnsureTableListVisible();
        if (!String.IsNullOrEmpty(StartupFolder)) {
          folderText.Text = StartupFolder;
          LoadFolder(StartupFolder);
        }
        CheckForUpdatesAsync();
      };
    }

    void ConfigureFilterButton(Button button, string text) {
      button.Text = text;
      button.Width = text.Length > 6 ? 82 : text.Length > 4 ? 70 : 46;
      button.Height = 30;
      button.Margin = new Padding(2, 0, 2, 0);
      button.BackColor = Theme.Surface;
      button.ForeColor = Theme.Muted;
    }

    void SetFlowFilter(string filter) {
      if (filter == "all") {
        canvas.ShowSources = true;
        canvas.ShowTables = true;
        canvas.ShowColumns = true;
        canvas.ShowMeasures = true;
        canvas.ShowPages = true;
      }
      UpdateFlowFilterButtons();
      canvas.Invalidate();
    }

    void ToggleFlowFilter(string filter) {
      if (filter == "source") canvas.ShowSources = !canvas.ShowSources;
      if (filter == "table") canvas.ShowTables = !canvas.ShowTables;
      if (filter == "column") canvas.ShowColumns = !canvas.ShowColumns;
      if (filter == "measure") canvas.ShowMeasures = !canvas.ShowMeasures;
      if (filter == "page") canvas.ShowPages = !canvas.ShowPages;
      if (!canvas.ShowSources && !canvas.ShowTables && !canvas.ShowColumns && !canvas.ShowMeasures && !canvas.ShowPages) {
        canvas.ShowSources = true;
        canvas.ShowTables = true;
        canvas.ShowColumns = true;
        canvas.ShowMeasures = true;
        canvas.ShowPages = true;
      }
      UpdateFlowFilterButtons();
      canvas.Invalidate();
    }

    void UpdateFlowFilterButtons() {
      var all = canvas.ShowSources && canvas.ShowTables && canvas.ShowColumns && canvas.ShowMeasures && canvas.ShowPages;
      StyleToggleButton(flowAllButton, all);
      StyleToggleButton(flowSourcesButton, canvas.ShowSources);
      StyleToggleButton(flowTablesButton, canvas.ShowTables);
      StyleToggleButton(flowColumnsButton, canvas.ShowColumns);
      StyleToggleButton(flowMeasuresButton, canvas.ShowMeasures);
      StyleToggleButton(flowPagesButton, canvas.ShowPages);
    }

    void StyleToggleButton(Button button, bool active) {
      button.BackColor = active ? Theme.PrimarySoft : Theme.Surface;
      button.ForeColor = active ? Theme.Primary : Theme.Muted;
      button.Invalidate();
    }

    void ToggleDetailsPane() {
      if (detailsCollapsed) {
        detailsCollapsed = false;
        right.Panel1MinSize = 120;
        details.Visible = true;
        detailsHost.RowStyles[1].Height = 100;
        detailsHost.RowStyles[1].SizeType = SizeType.Percent;
        detailsCollapseButton.Direction = ChevronDirection.Up;
        paneToolTip.SetToolTip(detailsCollapseButton, "Collapse Code Inspector");
        if (right.Height > previousDetailsHeight + right.Panel2MinSize + right.SplitterWidth) right.SplitterDistance = previousDetailsHeight;
      } else {
        detailsCollapsed = true;
        previousDetailsHeight = Math.Max(80, right.SplitterDistance);
        details.Visible = false;
        detailsHost.RowStyles[1].Height = 0;
        right.Panel1MinSize = 42;
        right.SplitterDistance = 42;
        detailsCollapseButton.Direction = ChevronDirection.Down;
        paneToolTip.SetToolTip(detailsCollapseButton, "Expand Code Inspector");
      }
    }

    void ToggleSidebarPane() {
      if (sidebarCollapsed) {
        sidebarCollapsed = false;
        body.Panel2Collapsed = false;
        body.Panel2MinSize = 220;
        sidebarCollapsedRail.Visible = false;
        sidebarHost.Visible = true;
        sidebarHost.BringToFront();
        var target = Math.Max(body.Panel1MinSize, body.Width - previousSidebarWidth - body.SplitterWidth);
        body.SplitterDistance = Math.Min(target, body.Width - body.Panel2MinSize - body.SplitterWidth);
      } else {
        sidebarCollapsed = true;
        previousSidebarWidth = Math.Max(220, body.Panel2.Width);
        sidebarHost.Visible = false;
        sidebarCollapsedRail.Visible = true;
        sidebarCollapsedRail.BringToFront();
        body.Panel2MinSize = 56;
        body.SplitterDistance = Math.Max(body.Panel1MinSize, body.Width - 56 - body.SplitterWidth);
      }
    }

    void BuildModelTabs() {
      modelTabs.Controls.Clear();
      AddModelTab("All tables");
      AddModelTab("Layout 1");
      var add = new PremiumButton();
      add.Text = "+";
      add.Width = 34;
      add.Height = 30;
      add.BackColor = Theme.Surface;
      add.ForeColor = Theme.Primary;
      add.Margin = new Padding(4, 0, 0, 0);
      add.Click += delegate {
        layoutTabCount++;
        var name = "Layout " + layoutTabCount;
        modelTabs.Controls.Remove(add);
        AddModelTab(name);
        modelTabs.Controls.Add(add);
        SelectModelPage(name);
      };
      modelTabs.Controls.Add(add);
      canvas.EnsureLayoutPage("All tables", true);
      canvas.EnsureLayoutPage("Layout 1", false);
      SelectModelPage("All tables");
    }

    void AddModelTab(string name) {
      var button = new PremiumButton();
      var canClose = !String.Equals(name, "All tables", StringComparison.OrdinalIgnoreCase);
      button.Text = canClose ? name + "  x" : name;
      button.Tag = name;
      button.AutoSize = true;
      button.Height = 30;
      button.MinimumSize = new Size(84, 30);
      button.Margin = new Padding(0, 0, 4, 0);
      button.MouseClick += delegate(object sender, MouseEventArgs e) {
        if (canClose && e.X >= button.Width - 22) {
          RemoveModelTab(button, name);
          return;
        }
        SelectModelPage(name);
      };
      modelTabs.Controls.Add(button);
    }

    void RemoveModelTab(Button button, string name) {
      modelTabs.Controls.Remove(button);
      canvas.DeleteLayoutPage(name);
      if (String.Equals(canvas.LayoutPage, name, StringComparison.OrdinalIgnoreCase)) SelectModelPage("All tables");
    }

    void SelectModelPage(string name) {
      canvas.EnsureLayoutPage(name, String.Equals(name, "All tables", StringComparison.OrdinalIgnoreCase));
      canvas.LayoutPage = name;
      foreach (Control control in modelTabs.Controls) {
        var button = control as Button;
        if (button == null || button.Tag == null) continue;
        var active = String.Equals(button.Tag.ToString(), name, StringComparison.OrdinalIgnoreCase);
        button.BackColor = active ? Theme.PrimarySoft : Theme.Surface;
        button.ForeColor = active ? Theme.Primary : Theme.Muted;
        button.Invalidate();
      }
      canvas.Invalidate();
    }

    void UpdateModelTabsVisibility() {
      var isDataModel = canvas.ViewMode == "datamodel";
      modelTabs.Visible = isDataModel;
      flowToolbarHost.Visible = true;
      flowFilterBar.Visible = !isDataModel;
      canvasHost.RowStyles[0].SizeType = SizeType.Absolute;
      canvasHost.RowStyles[0].Height = 52;
      canvasHost.RowStyles[2].SizeType = SizeType.Absolute;
      canvasHost.RowStyles[2].Height = isDataModel ? 38 : 0;
    }

    void EnsureTableListVisible() {
      if (!sidebarCollapsed && body.Width > 900) {
        body.Panel1MinSize = 520;
        body.Panel2MinSize = 220;
        var desiredRight = 240;
        var maxLeft = body.Width - body.Panel2MinSize - body.SplitterWidth;
        var target = Math.Min(Math.Max(body.Panel1MinSize, body.Width - desiredRight - body.SplitterWidth), maxLeft);
        if (body.SplitterDistance < body.Panel1MinSize || body.SplitterDistance > maxLeft || body.Panel2.Width < 180 || body.Panel2.Width > 320) {
          body.SplitterDistance = target;
        }
      }
      if (!detailsCollapsed && right.Height > 120 + 220 + right.SplitterWidth) {
        right.Panel1MinSize = 120;
        right.Panel2MinSize = 220;
        var target = Math.Min(230, right.Height - right.Panel2MinSize - right.SplitterWidth);
        right.SplitterDistance = Math.Max(right.Panel1MinSize, target);
      }
    }

    void SetViewMode(string mode) {
      canvas.ViewMode = mode;
      dataFlowButton.BackColor = mode == "dataflow" ? Theme.PrimarySoft : Theme.Surface;
      dataFlowButton.ForeColor = mode == "dataflow" ? Theme.Primary : Theme.Muted;
      dataModelButton.BackColor = mode == "datamodel" ? Theme.PrimarySoft : Theme.Surface;
      dataModelButton.ForeColor = mode == "datamodel" ? Theme.Primary : Theme.Muted;
      dataFlowButton.Invalidate();
      dataModelButton.Invalidate();
      exportButton.Enabled = mode == "dataflow";
      exportButton.Invalidate();
      UpdateFlowFilterButtons();
      UpdateModelTabsVisibility();
      ApplyViewMode();
    }

    void ApplyViewMode() {
      canvas.Invalidate();
      if (canvas.ViewMode == "datamodel" && canvas.SelectedRelationship != null) RenderRelationshipDetails(canvas.SelectedRelationship);
      else RenderDetails(graph.NodeById(canvas.SelectedId));
    }

    async void CheckForUpdatesAsync() {
      try {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
        string json;
        using (var client = new UpdateWebClient()) json = await client.DownloadStringTaskAsync(new Uri(UpdateManifestUrl));
        if (String.IsNullOrWhiteSpace(json) || json.Length > 65536) return;

        var manifest = new JavaScriptSerializer().Deserialize<UpdateManifest>(json);
        if (!ValidManifest(manifest)) return;

        if (CompareApplicationVersions(manifest.version, CurrentApplicationVersion()) <= 0) return;
        if (IsDisposed || !IsHandleCreated) return;

        availableUpdate = manifest;
        updateMenuItem.Text = "Update available  " + manifest.version;
        updateMenuItem.Visible = true;
      } catch {
        // Update checks never interrupt normal startup or offline use.
      }
    }

    async void InstallAvailableUpdateAsync() {
      if (updateInstalling || availableUpdate == null) return;
      var answer = MessageBox.Show(this,
        "PBI Lineage Studio " + availableUpdate.version + " is available." + Environment.NewLine + Environment.NewLine +
        "Download the update and restart now?",
        "Update available", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
      if (answer != DialogResult.Yes) return;

      updateInstalling = true;
      updateMenuItem.Enabled = false;
      updateMenuItem.Text = "Downloading update...";
      Cursor = Cursors.WaitCursor;
      string updateFolder = null;
      string downloadPath = null;
      string updateStage = "creating the private update folder";
      try {
        updateFolder = UpdateStorage.CreateFolder(availableUpdate.version);
        var stagedPath = Path.Combine(updateFolder, "PBI-Lineage-Studio.exe");
        downloadPath = stagedPath + ".download";

        updateStage = "downloading the release";
        using (var client = new UpdateWebClient()) await client.DownloadFileTaskAsync(new Uri(availableUpdate.downloadUrl), downloadPath);
        updateStage = "verifying the release checksum";
        var actualHash = Sha256(downloadPath);
        if (!actualHash.Equals(availableUpdate.sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The downloaded file did not match the release checksum.");
        updateStage = "staging the verified executable";
        File.Move(downloadPath, stagedPath);

        updateStage = "starting the update installer";
        var currentPath = Assembly.GetExecutingAssembly().Location;
        var helper = Process.Start(new ProcessStartInfo {
          FileName = stagedPath,
          Arguments = "--apply-update " + QuoteArgument(currentPath) + " " + Process.GetCurrentProcess().Id,
          WorkingDirectory = updateFolder,
          UseShellExecute = true
        });
        if (helper == null) throw new InvalidOperationException("The update installer could not be started.");
        UpdateDiagnostics.Write("Started update helper PID " + helper.Id + " from " + stagedPath + ". Closing old application.");
        updateMenuItem.Text = "Restarting...";
        Application.Exit();
      } catch (Exception ex) {
        if (!String.IsNullOrEmpty(updateFolder)) UpdateStorage.TryDeleteManagedFolder(updateFolder);
        updateInstalling = false;
        updateMenuItem.Enabled = true;
        updateMenuItem.Text = "Update available  " + availableUpdate.version;
        MessageBox.Show(this, "The update could not be downloaded or verified." + Environment.NewLine + Environment.NewLine +
          "Step: " + updateStage + Environment.NewLine +
          "Folder: " + (updateFolder ?? UpdateStorage.RootPath) + Environment.NewLine + Environment.NewLine + ex.Message,
          "PBI Lineage Studio Update", MessageBoxButtons.OK, MessageBoxIcon.Error);
      } finally {
        Cursor = Cursors.Default;
      }
    }

    static bool ValidManifest(UpdateManifest manifest) {
      if (manifest == null || String.IsNullOrWhiteSpace(manifest.version) || String.IsNullOrWhiteSpace(manifest.downloadUrl)) return false;
      if (manifest.version.Length > 128 || !Regex.IsMatch(manifest.version, "^\\d+(?:\\.\\d+){2,}$")) return false;
      if (String.IsNullOrWhiteSpace(manifest.sha256) || !Regex.IsMatch(manifest.sha256, "^[A-Fa-f0-9]{64}$")) return false;
      Uri downloadUri;
      return Uri.TryCreate(manifest.downloadUrl, UriKind.Absolute, out downloadUri) &&
        downloadUri.Scheme == Uri.UriSchemeHttps && downloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
    }

    static int CompareApplicationVersions(string left, string right) {
      var leftParts = left.Split('.');
      var rightParts = right.Split('.');
      var count = Math.Max(leftParts.Length, rightParts.Length);
      for (var index = 0; index < count; index++) {
        var leftPart = NormalizeVersionPart(index < leftParts.Length ? leftParts[index] : "0");
        var rightPart = NormalizeVersionPart(index < rightParts.Length ? rightParts[index] : "0");
        if (leftPart.Length != rightPart.Length) return leftPart.Length.CompareTo(rightPart.Length);
        var comparison = String.CompareOrdinal(leftPart, rightPart);
        if (comparison != 0) return comparison;
      }
      return 0;
    }

    static string NormalizeVersionPart(string value) {
      var normalized = (value ?? "0").TrimStart('0');
      return normalized.Length == 0 ? "0" : normalized;
    }

    static string CurrentApplicationVersion() {
      var attributes = Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
      if (attributes.Length > 0) {
        var informational = attributes[0] as AssemblyInformationalVersionAttribute;
        if (informational != null && !String.IsNullOrWhiteSpace(informational.InformationalVersion)) return informational.InformationalVersion;
      }
      var version = Assembly.GetExecutingAssembly().GetName().Version;
      return version.Major + "." + version.Minor + "." + version.Build + (version.Revision > 0 ? "." + version.Revision : "");
    }

    static string Sha256(string path) {
      using (var stream = File.OpenRead(path))
      using (var sha = SHA256.Create()) {
        var hash = sha.ComputeHash(stream);
        var text = new StringBuilder(hash.Length * 2);
        foreach (var value in hash) text.Append(value.ToString("x2"));
        return text.ToString();
      }
    }

    static string QuoteArgument(string value) {
      return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
    }

    void ShowAbout() {
      MessageBox.Show(this,
        "PBI Lineage Studio" + Environment.NewLine +
        "Version " + CurrentApplicationVersion() + Environment.NewLine + Environment.NewLine +
        "Local Power BI semantic model lineage viewer.",
        "About PBI Lineage Studio", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void ExportPng() {
      if (canvas.ViewMode != "dataflow") {
        MessageBox.Show(this, "PNG export is currently available in Data Flow view.", "Export PNG", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
      }
      if (graph.Nodes.Count == 0) {
        MessageBox.Show(this, "Load a semantic model before exporting.", "Export PNG", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
      }

      var selected = graph.NodeById(canvas.SelectedId);
      var selectedName = selected == null ? "" : selected.Display;
      using (var options = new ExportOptionsDialog(selectedName)) {
        if (options.ShowDialog(this) != DialogResult.OK) return;
        using (var save = new SaveFileDialog()) {
          save.Title = "Export complete Data Flow";
          save.Filter = "PNG image (*.png)|*.png";
          save.DefaultExt = "png";
          save.AddExtension = true;
          save.FileName = selected == null ? "power-bi-data-flow.png" : SafeFileName(selected.Display) + "-lineage.png";
          if (save.ShowDialog(this) != DialogResult.OK) return;
          try {
            status.Text = "Exporting the complete Data Flow...";
            Cursor = Cursors.WaitCursor;
            using (var bitmap = canvas.ExportDataFlowImage(options.IncludeDetails ? details.Text : "")) {
              bitmap.Save(save.FileName, System.Drawing.Imaging.ImageFormat.Png);
            }
            status.Text = "Exported PNG: " + save.FileName;
          } catch (Exception ex) {
            status.Text = "PNG export failed: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Could not export PNG", MessageBoxButtons.OK, MessageBoxIcon.Error);
          } finally {
            Cursor = Cursors.Default;
          }
        }
      }
    }

    string SafeFileName(string value) {
      var name = value ?? "data-flow";
      foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
      if (name.Length > 80) name = name.Substring(0, 80);
      return name.Length == 0 ? "data-flow" : name;
    }

    void BrowseFolder() {
      using (var dialog = new FolderBrowserDialog()) {
        dialog.Description = "Select .SemanticModel, definition, or definition\\tables folder";
        if (Directory.Exists(folderText.Text)) dialog.SelectedPath = folderText.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK) {
          folderText.Text = dialog.SelectedPath;
          LoadFolder(dialog.SelectedPath);
        }
      }
    }

    public void LoadFolder(string path) {
      try {
        var root = ResolveTablesFolder(path);
        var tableFiles = Directory.GetFiles(root, "*.tmdl", SearchOption.TopDirectoryOnly)
          .Select(p => new TmdlFile { Path = p, Content = File.ReadAllText(p) })
          .ToList();
        var files = LoadModelTmdlFiles(root, tableFiles);
        if (tableFiles.Count == 0) {
          status.Text = "No .tmdl table files found in " + root;
          tableBrowser.LoadedFiles = 0;
          tableBrowser.Message = status.Text;
          PopulateList();
          return;
        }
        graph = TmdlParser.Parse(files);
        var reportInfo = ReportParser.AttachReportPages(graph, root);
        canvas.Graph = graph;
        canvas.EnsureLayoutPage("All tables", true);
        canvas.EnsureLayoutPage("Layout 1", false);
        canvas.SelectedId = null;
        canvas.ResetView();
        ApplyViewMode();
        details.Text = "";
        tableBrowser.LoadedFiles = tableFiles.Count;
        tableBrowser.Message = "";
        tableBrowser.CollapseAllTablesOnNextRebuild();
        PopulateList();
        EnsureTableListVisible();
        flowPagesButton.Enabled = reportInfo.Pages > 0;
        status.Text = "Loaded " + tableFiles.Count + " table files and " + (files.Count - tableFiles.Count) + " model files, " + graph.Nodes.Count + " objects, " + graph.Edges.Count + " flows. " + tableBrowser.TableCount + " tables, " + reportInfo.Pages + " report pages, " + reportInfo.MeasureLinks + " page usages.";
      } catch (Exception ex) {
        status.Text = ex.Message;
        tableBrowser.LoadedFiles = 0;
        tableBrowser.Message = ex.Message;
        flowPagesButton.Enabled = false;
        PopulateList();
      }
    }

    string ResolveTablesFolder(string path) {
      var root = (path ?? "").Trim().Trim('"');
      if (root.Length == 0) throw new Exception("Folder path is required.");
      if (!Directory.Exists(root)) {
        var resolved = ResolveRelativeSemanticModelPath(root);
        if (resolved.Length == 0) throw new Exception("Folder not found: " + root);
        root = resolved;
      }
      var directTables = System.IO.Path.Combine(root, "tables");
      if (Directory.Exists(directTables)) return directTables;
      var definitionTables = System.IO.Path.Combine(root, "definition", "tables");
      if (Directory.Exists(definitionTables)) return definitionTables;
      return root;
    }

    List<TmdlFile> LoadModelTmdlFiles(string tablesRoot, List<TmdlFile> tableFiles) {
      var files = new List<TmdlFile>(tableFiles);
      var seen = new HashSet<string>(tableFiles.Select(f => System.IO.Path.GetFullPath(f.Path)), StringComparer.OrdinalIgnoreCase);
      var tableDir = new DirectoryInfo(tablesRoot);
      var definitionDir = tableDir.Name.Equals("tables", StringComparison.OrdinalIgnoreCase) ? tableDir.Parent : tableDir;
      if (definitionDir == null || !definitionDir.Exists) return files;
      foreach (var path in Directory.GetFiles(definitionDir.FullName, "*.tmdl", SearchOption.AllDirectories)) {
        var full = System.IO.Path.GetFullPath(path);
        if (seen.Contains(full)) continue;
        seen.Add(full);
        files.Add(new TmdlFile { Path = path, Content = File.ReadAllText(path) });
      }
      return files;
    }

    string ResolveRelativeSemanticModelPath(string root) {
      if (System.IO.Path.IsPathRooted(root)) return "";
      var normalized = root.Replace('/', '\\').Trim('\\');
      var baseDir = AppDomain.CurrentDomain.BaseDirectory;
      var direct = System.IO.Path.Combine(baseDir, normalized);
      if (Directory.Exists(direct)) return direct;
      try {
        foreach (var dir in Directory.GetDirectories(baseDir, "definition", SearchOption.AllDirectories)) {
          if (dir.IndexOf(".SemanticModel", StringComparison.OrdinalIgnoreCase) >= 0) return dir;
        }
      } catch {
      }
      return "";
    }

    void PopulateList() {
      tableBrowser.Graph = graph;
      tableBrowser.Query = searchText.Text ?? "";
      tableBrowser.SelectedId = canvas.SelectedId;
      tableBrowser.Rebuild();
      tableBrowser.Visible = !sidebarCollapsed;
      if (!sidebarCollapsed) tableBrowser.BringToFront();
      EnsureTableListVisible();
    }

    List<Node> TableListNodes() {
      var tables = graph.Nodes.Where(n => n.Type == "table").OrderBy(n => n.Display).ToList();
      if (tables.Count > 0) return tables;
      var names = graph.Nodes
        .Where(n => !String.IsNullOrEmpty(n.Table))
        .Select(n => n.Table)
        .Distinct()
        .OrderBy(n => n)
        .ToList();
      return names.Select(name => new Node("table:" + name, name, "table", name, "", "")).ToList();
    }

    IEnumerable<Node> SourceNodesForTable(Node table) {
      var ids = new HashSet<string>();
      var tableId = "table:" + table.Table;
      foreach (var edge in graph.Edges.Where(e => e.To == tableId)) {
        ids.Add(edge.From);
        foreach (var parent in graph.Edges.Where(e => e.To == edge.From)) ids.Add(parent.From);
      }
      return ids.Select(id => graph.NodeById(id)).Where(n => n != null && (n.Type == "source" || n.Type == "source-schema" || n.Type == "source-table"));
    }

    bool MatchesQuery(Node node, string query) {
      return (node.Display + " " + node.Type + " " + node.Table).ToLowerInvariant().Contains(query);
    }

    string AssetName(Node node) {
      if (node.Type == "source" || node.Type == "source-schema" || node.Type == "source-table") return node.Display;
      var prefix = node.Table + "[";
      if (node.Display.StartsWith(prefix) && node.Display.EndsWith("]")) {
        return node.Display.Substring(prefix.Length, node.Display.Length - prefix.Length - 1);
      }
      return node.Display;
    }

    int TypeRank(string type) {
      if (type == "source") return 0;
      if (type == "source-schema") return 1;
      if (type == "source-table") return 2;
      if (type == "table") return 3;
      if (type == "column") return 4;
      if (type == "measure") return 5;
      if (type == "page") return 6;
      return 9;
    }

    void SelectObjectById(string id) {
      var node = graph.NodeById(id);
      if (node == null) {
        tableBrowser.SelectedId = null;
        tableBrowser.Invalidate();
        canvas.SelectedId = null;
        canvas.SelectedRelationship = null;
        canvas.Invalidate();
        RenderDetails(null);
        return;
      }
      canvas.SelectedId = id;
      canvas.SelectedRelationship = null;
      tableBrowser.SelectedId = id;
      tableBrowser.EnsureSelectedVisible();
      tableBrowser.Invalidate();
      canvas.Invalidate();
      RenderDetails(node);
    }

    void SelectRelationship(Edge edge) {
      if (edge == null) return;
      canvas.SelectedId = null;
      canvas.SelectedRelationship = edge;
      tableBrowser.SelectedId = null;
      tableBrowser.Invalidate();
      canvas.Invalidate();
      RenderRelationshipDetails(edge);
    }

    void RenderDetails(Node node) {
      if (node == null) {
        details.Text = "";
        return;
      }
      if (node.Type == "page") {
        var pageMeasures = graph.Edges.Where(e => e.To == node.Id && (e.Kind == "page-usage" || e.Kind == "report-filter"))
          .Select(e => graph.NodeById(e.From)).Where(n => n != null && n.Type == "measure").OrderBy(n => n.Display).ToList();
        details.Text =
          "REPORT PAGE: " + node.Display + Environment.NewLine + Environment.NewLine +
          (node.Table.Length > 0 ? "Report" + Environment.NewLine + node.Table + Environment.NewLine + Environment.NewLine : "") +
          (node.Expression.Length > 0 ? node.Expression + Environment.NewLine + Environment.NewLine : "") +
          "Measures used on this page" + Environment.NewLine + FormatList(pageMeasures);
        return;
      }
      var upstream = graph.Upstream(node.Id, canvas.ViewMode == "datamodel").Select(id => graph.NodeById(id)).Where(n => n != null).ToList();
      var downstream = graph.Downstream(node.Id, canvas.ViewMode == "datamodel").Select(id => graph.NodeById(id)).Where(n => n != null).ToList();
      details.Text =
        node.Type.ToUpperInvariant() + ": " + node.Display + Environment.NewLine + Environment.NewLine +
        (node.Expression.Length > 0 ? ExpressionLabel(node) + Environment.NewLine + node.Expression + Environment.NewLine + Environment.NewLine : "") +
        "View" + Environment.NewLine + (canvas.ViewMode == "datamodel" ? "Data Model View" : "Data Flow View") + Environment.NewLine + Environment.NewLine +
        "Upstream inputs" + Environment.NewLine + FormatList(upstream) + Environment.NewLine +
        "Downstream impact" + Environment.NewLine + FormatList(downstream);
    }

    void RenderRelationshipDetails(Edge edge) {
      if (edge == null) {
        details.Text = "";
        return;
      }
      var from = graph.NodeById(edge.From);
      var to = graph.NodeById(edge.To);
      details.Text =
        "RELATIONSHIP" + Environment.NewLine + Environment.NewLine +
        "From" + Environment.NewLine +
        "Table: " + TableName(from) + Environment.NewLine +
        "Column: " + ColumnName(from) + Environment.NewLine + Environment.NewLine +
        "To" + Environment.NewLine +
        "Table: " + TableName(to) + Environment.NewLine +
        "Column: " + ColumnName(to) + Environment.NewLine + Environment.NewLine +
        "Cardinality" + Environment.NewLine +
        CardinalityLabel(edge.FromCardinality, edge.ToCardinality) + Environment.NewLine + Environment.NewLine +
        "Cross filter direction" + Environment.NewLine +
        DirectionLabel(edge, from, to) + Environment.NewLine + Environment.NewLine +
        "Active status" + Environment.NewLine +
        (edge.IsActive ? "Active" : "Inactive");
    }

    string TableName(Node node) {
      return node == null ? "" : node.Table;
    }

    string ColumnName(Node node) {
      if (node == null) return "";
      var prefix = node.Table + "[";
      if (node.Display.StartsWith(prefix) && node.Display.EndsWith("]")) return node.Display.Substring(prefix.Length, node.Display.Length - prefix.Length - 1);
      return node.Display;
    }

    string CardinalityLabel(string from, string to) {
      var left = from == "1" ? "One" : "Many";
      var right = to == "1" ? "One" : "Many";
      return left + " to " + right + " (" + (String.IsNullOrEmpty(from) ? "*" : from) + ":" + (String.IsNullOrEmpty(to) ? "1" : to) + ")";
    }

    string DirectionLabel(Edge edge, Node from, Node to) {
      var raw = (edge.Direction ?? "").Trim();
      if (raw.IndexOf("both", StringComparison.OrdinalIgnoreCase) >= 0) return "Both directions";
      if (edge.FromCardinality == "1" && edge.ToCardinality == "*") return "Single (" + TableName(from) + " filters " + TableName(to) + ")";
      if (edge.FromCardinality == "*" && edge.ToCardinality == "1") return "Single (" + TableName(to) + " filters " + TableName(from) + ")";
      if (raw.Length == 0) return "Single (" + TableName(to) + " filters " + TableName(from) + ")";
      return raw;
    }

    string ExpressionLabel(Node node) {
      return node.Type == "source" || node.Type == "source-schema" || node.Type == "source-table" || node.Type == "table" ? "POWER QUERY / SOURCE" : "DAX";
    }

    string FormatList(List<Node> nodes) {
      if (nodes.Count == 0) return "None" + Environment.NewLine;
      return String.Join(Environment.NewLine, nodes.Select(n => "- " + n.Type + ": " + n.Display).ToArray()) + Environment.NewLine;
    }
  }

  public class TableBrowser : Panel {
    public const string TableDragFormat = "PbiLineageStudio.TableId";
    public Graph Graph = new Graph();
    public string Query = "";
    public string SelectedId;
    public int LoadedFiles;
    public string Message = "";
    public int TableCount { get { return TableNodes().Count; } }
    public int PageCount { get { return Graph.Nodes.Count(n => n.Type == "page"); } }
    public event Action<string> SelectionChanged;
    readonly List<TableBrowserRow> rows = new List<TableBrowserRow>();
    readonly HashSet<string> expandedTables = new HashSet<string>();
    readonly HashSet<string> expandedGroups = new HashSet<string>();
    int horizontalOffset;
    int hoveredRow = -1;
    int dragRowIndex = -1;
    Point dragStart;
    const int RowHeight = 30;

    public TableBrowser() {
      DoubleBuffered = true;
      AutoScroll = true;
      TabStop = true;
      BorderStyle = BorderStyle.None;
      BackColor = Theme.Surface;
      ForeColor = Theme.Ink;
      Font = new Font("Segoe UI", 9F);
      expandedGroups.Add("report-pages");
    }

    public void Rebuild() {
      rows.Clear();
      var query = (Query ?? "").Trim().ToLowerInvariant();
      var pages = Graph.Nodes.Where(n => n.Type == "page" && (query.Length == 0 || Matches(n, query))).OrderBy(n => n.Display).ToList();
      if (pages.Count > 0) {
        var pageGroupCollapsed = query.Length == 0 && !expandedGroups.Contains("report-pages");
        rows.Add(new TableBrowserRow { Text = "Pages (" + pages.Count + ")", Indent = 0, IsGroup = true, Key = "report-pages", CanCollapse = true, IsCollapsed = pageGroupCollapsed });
        if (!pageGroupCollapsed) foreach (var page in pages) rows.Add(new TableBrowserRow { Text = page.Display, Node = page, Indent = 1 });
      }
      var tables = TableNodes();
      foreach (var table in tables) {
        var sources = SourceNodesForTable(table).OrderBy(n => TypeRank(n.Type)).ThenBy(n => n.Display).ToList();
        var columns = Graph.Nodes.Where(n => n.Table == table.Table && n.Type == "column").OrderBy(n => AssetName(n)).ToList();
        var measures = Graph.Nodes.Where(n => n.Table == table.Table && n.Type == "measure").OrderBy(n => AssetName(n)).ToList();
        var allChildren = sources.Concat(columns).Concat(measures).ToList();
        if (query.Length > 0 && !Matches(table, query) && !allChildren.Any(n => Matches(n, query))) continue;

        var tableKey = TableKey(table);
        var isCollapsed = !expandedTables.Contains(tableKey);
        rows.Add(new TableBrowserRow { Text = table.Display, Node = table, Indent = 0, IsTable = true, Key = tableKey, CanCollapse = true, IsCollapsed = isCollapsed });
        if (isCollapsed) continue;
        AddGroup(tableKey, "Source", sources, query);
        AddGroup(tableKey, "Columns", columns, query);
        AddGroup(tableKey, "Measures", measures, query);
      }
      if (rows.Count == 0) {
        var message = !String.IsNullOrEmpty(Message)
          ? Message
          : LoadedFiles == 0
          ? "Load a model folder to show tables"
          : "Loaded " + LoadedFiles + " files, but no table nodes were parsed";
        if (Graph.Nodes.Count > 0) message = "No tables found in " + Graph.Nodes.Count + " parsed objects";
        rows.Add(new TableBrowserRow { Text = message, Indent = 0, IsMessage = true });
      }
      AutoScrollMinSize = new Size(MeasureContentWidth(), rows.Count * RowHeight + 8);
      horizontalOffset = Math.Min(horizontalOffset, Math.Max(0, AutoScrollMinSize.Width - ClientSize.Width));
      Invalidate();
    }

    void AddGroup(string tableKey, string name, List<Node> nodes, string query) {
      var visible = nodes.Where(n => query.Length == 0 || Matches(n, query)).ToList();
      if (visible.Count == 0) return;
      var groupKey = tableKey + ":" + name;
      var isCollapsed = !expandedGroups.Contains(groupKey);
      rows.Add(new TableBrowserRow { Text = name + " (" + visible.Count + ")", Indent = 1, IsGroup = true, Key = groupKey, CanCollapse = true, IsCollapsed = isCollapsed });
      if (isCollapsed) return;
      foreach (var node in visible) rows.Add(new TableBrowserRow { Text = AssetName(node), Node = node, Indent = 2 });
    }

    string TableKey(Node table) {
      return "table:" + table.Id;
    }

    public void CollapseAllTablesOnNextRebuild() {
      expandedTables.Clear();
      expandedGroups.Clear();
      expandedGroups.Add("report-pages");
    }

    int MeasureContentWidth() {
      var max = ClientSize.Width;
      using (var g = CreateGraphics()) {
        foreach (var row in rows) {
          var width = 28 + row.Indent * 18 + (row.CanCollapse ? 14 : 0) + TextRenderer.MeasureText(g, row.Text ?? "", Font).Width;
          max = Math.Max(max, width);
        }
      }
      return max;
    }

    protected override void OnPaint(PaintEventArgs e) {
      base.OnPaint(e);
      e.Graphics.Clear(Theme.Surface);
      e.Graphics.TranslateTransform(-horizontalOffset, AutoScrollPosition.Y);
      if (rows.Count == 0) Rebuild();
      for (var i = 0; i < rows.Count; i++) {
        var row = rows[i];
        var rect = new Rectangle(0, i * RowHeight, ClientSize.Width, RowHeight);
        if (rect.Bottom + AutoScrollPosition.Y < 0 || rect.Top + AutoScrollPosition.Y > ClientSize.Height) continue;
        var selected = row.Node != null && row.Node.Id == SelectedId;
        var hovered = i == hoveredRow && row.Node != null;
        using (var back = new SolidBrush(selected ? Theme.PrimarySoft : hovered ? Theme.SurfaceMuted : row.IsGroup ? Theme.SurfaceMuted : Theme.Surface)) {
          e.Graphics.FillRectangle(back, rect);
        }
        if (selected) using (var accent = new SolidBrush(Theme.Primary)) e.Graphics.FillRectangle(accent, 0, rect.Top + 4, 3, rect.Height - 8);
        if (row.IsTable || row.IsGroup) using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 14, rect.Bottom - 1, ClientSize.Width - 14, rect.Bottom - 1);
        var x = 14 + row.Indent * 18;
        if (row.CanCollapse) {
          using (var brush = new SolidBrush(Theme.Muted)) {
            e.Graphics.DrawString(row.IsCollapsed ? ">" : "v", Font, brush, x, rect.Top + 7);
          }
          x += 14;
        }
        if (row.Node != null && !row.IsTable) {
          var dot = row.Node.Type == "page" ? Color.FromArgb(219, 39, 119) : row.Node.Type == "measure" ? Theme.Primary : row.Node.Type == "column" ? Theme.Green : Theme.Cyan;
          using (var dotBrush = new SolidBrush(dot)) e.Graphics.FillEllipse(dotBrush, x + 1, rect.Top + 12, 6, 6);
          x += 13;
        }
        var style = row.IsTable ? FontStyle.Bold : row.IsMessage ? FontStyle.Italic : FontStyle.Regular;
        var color = selected ? Theme.Primary : row.IsGroup || row.IsMessage ? Theme.Muted : Theme.Ink;
        using (var font = new Font(Font, style))
        using (var brush = new SolidBrush(color)) {
          e.Graphics.DrawString(row.Text, font, brush, x, rect.Top + 7);
        }
      }
    }

    protected override void OnMouseClick(MouseEventArgs e) {
      Focus();
      var y = e.Y - AutoScrollPosition.Y;
      var index = y / RowHeight;
      if (index >= 0 && index < rows.Count) {
        var row = rows[index];
        if (row.CanCollapse && !String.IsNullOrEmpty(row.Key)) {
          if (row.IsTable) {
            if (expandedTables.Contains(row.Key)) expandedTables.Remove(row.Key);
            else expandedTables.Add(row.Key);
          } else {
            if (expandedGroups.Contains(row.Key)) expandedGroups.Remove(row.Key);
            else expandedGroups.Add(row.Key);
          }
          Rebuild();
          return;
        }
        var node = row.Node;
        if (node != null) {
          SelectedId = node.Id;
          Invalidate();
          if (SelectionChanged != null) SelectionChanged(node.Id);
        }
      }
      base.OnMouseClick(e);
    }

    protected override void OnMouseDown(MouseEventArgs e) {
      dragRowIndex = RowIndexAt(e.Y);
      dragStart = e.Location;
      base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e) {
      var nextHover = RowIndexAt(e.Y);
      if (nextHover != hoveredRow) { hoveredRow = nextHover; Invalidate(); }
      if ((e.Button & MouseButtons.Left) == MouseButtons.Left && dragRowIndex >= 0) {
        var dx = Math.Abs(e.X - dragStart.X);
        var dy = Math.Abs(e.Y - dragStart.Y);
        if (dx >= SystemInformation.DragSize.Width / 2 || dy >= SystemInformation.DragSize.Height / 2) {
          var row = dragRowIndex < rows.Count ? rows[dragRowIndex] : null;
          dragRowIndex = -1;
          if (row != null && row.IsTable && row.Node != null) {
            DoDragDrop(new DataObject(TableDragFormat, row.Node.Id), DragDropEffects.Copy);
            return;
          }
        }
      }
      base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) {
      hoveredRow = -1;
      Invalidate();
      base.OnMouseLeave(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) {
      dragRowIndex = -1;
      base.OnMouseUp(e);
    }

    protected override void OnMouseEnter(EventArgs e) {
      Focus();
      base.OnMouseEnter(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e) {
      if ((ModifierKeys & Keys.Shift) == Keys.Shift) {
        ScrollHorizontal(-e.Delta);
        return;
      }
      base.OnMouseWheel(e);
    }

    protected override void WndProc(ref Message m) {
      const int WM_MOUSEHWHEEL = 0x020E;
      if (m.Msg == WM_MOUSEHWHEEL) {
        var delta = (short)((m.WParam.ToInt64() >> 16) & 0xffff);
        ScrollHorizontal(-delta);
        return;
      }
      base.WndProc(ref m);
    }

    void ScrollHorizontal(int delta) {
      var maxX = Math.Max(0, AutoScrollMinSize.Width - ClientSize.Width);
      horizontalOffset = Math.Max(0, Math.Min(maxX, horizontalOffset + delta));
      Invalidate();
    }

    int RowIndexAt(int y) {
      var adjusted = y - AutoScrollPosition.Y;
      var index = adjusted / RowHeight;
      return index >= 0 && index < rows.Count ? index : -1;
    }

    public void EnsureSelectedVisible() {
      if (String.IsNullOrEmpty(SelectedId)) return;
      for (var i = 0; i < rows.Count; i++) {
        if (rows[i].Node != null && rows[i].Node.Id == SelectedId) {
          AutoScrollPosition = new Point(0, Math.Max(0, i * RowHeight - RowHeight));
          return;
        }
      }
    }

    List<Node> TableNodes() {
      var tables = Graph.Nodes.Where(n => n.Type == "table").OrderBy(n => n.Display).ToList();
      if (tables.Count > 0) return tables;
      var names = Graph.Nodes.Where(n => !String.IsNullOrEmpty(n.Table)).Select(n => n.Table).Distinct().OrderBy(n => n).ToList();
      return names.Select(name => new Node("table:" + name, name, "table", name, "", "")).ToList();
    }

    IEnumerable<Node> SourceNodesForTable(Node table) {
      var ids = new HashSet<string>();
      var tableId = "table:" + table.Table;
      foreach (var edge in Graph.Edges.Where(e => e.To == tableId)) {
        ids.Add(edge.From);
        AddParents(edge.From, ids);
      }
      return ids.Select(id => Graph.NodeById(id)).Where(n => n != null && (n.Type == "source" || n.Type == "source-schema" || n.Type == "source-table"));
    }

    void AddParents(string id, HashSet<string> ids) {
      foreach (var edge in Graph.Edges.Where(e => e.To == id)) {
        if (ids.Contains(edge.From)) continue;
        ids.Add(edge.From);
        AddParents(edge.From, ids);
      }
    }

    bool Matches(Node node, string query) {
      return (node.Display + " " + node.Type + " " + node.Table).ToLowerInvariant().Contains(query);
    }

    string AssetName(Node node) {
      if (node.Type == "source" || node.Type == "source-schema" || node.Type == "source-table") return node.Display;
      var prefix = node.Table + "[";
      if (node.Display.StartsWith(prefix) && node.Display.EndsWith("]")) return node.Display.Substring(prefix.Length, node.Display.Length - prefix.Length - 1);
      return node.Display;
    }

    int TypeRank(string type) {
      if (type == "source") return 0;
      if (type == "source-schema") return 1;
      if (type == "source-table") return 2;
      if (type == "table") return 3;
      if (type == "column") return 4;
      if (type == "measure") return 5;
      if (type == "page") return 6;
      return 9;
    }
  }

  public class TableBrowserRow {
    public string Text;
    public Node Node;
    public string Key;
    public int Indent;
    public bool IsTable;
    public bool IsGroup;
    public bool IsMessage;
    public bool CanCollapse;
    public bool IsCollapsed;
  }

  public class LineageCanvas : Panel {
    public Graph Graph = new Graph();
    public string SelectedId;
    public Edge SelectedRelationship;
    public string ViewMode = "dataflow";
    public string LayoutPage = "All tables";
    public float Zoom = 1.0F;
    public bool ShowSources = true;
    public bool ShowTables = true;
    public bool ShowColumns = true;
    public bool ShowMeasures = true;
    public bool ShowPages = true;
    public event Action<string> SelectionChanged;
    public event Action<Edge> RelationshipSelectionChanged;
    readonly Dictionary<string, Rectangle> positions = new Dictionary<string, Rectangle>();
    readonly Dictionary<string, Point> flowPositions = new Dictionary<string, Point>();
    readonly Dictionary<string, LayoutPageState> pageStates = new Dictionary<string, LayoutPageState>(StringComparer.OrdinalIgnoreCase);
    readonly List<RelationshipHit> relationshipHits = new List<RelationshipHit>();
    readonly List<RelationshipOverlay> relationshipOverlays = new List<RelationshipOverlay>();
    readonly ToolTip nodeToolTip = new ToolTip();
    string hoverNodeId;
    string dragNodeId;
    Point dragOffset;
    bool dragMoved;

    public LineageCanvas() {
      DoubleBuffered = true;
      BackColor = Theme.Window;
      AutoScroll = true;
      AllowDrop = true;
      TabStop = true;
      nodeToolTip.InitialDelay = 350;
      nodeToolTip.ReshowDelay = 100;
      nodeToolTip.AutoPopDelay = 8000;
    }

    public void EnsureLayoutPage(string name, bool isAllTablesPage) {
      var state = PageState(name);
      state.IsAllTablesPage = isAllTablesPage;
      if (isAllTablesPage) state.TableIds.Clear();
    }

    public void DeleteLayoutPage(string name) {
      if (String.Equals(name, "All tables", StringComparison.OrdinalIgnoreCase)) return;
      pageStates.Remove(name);
      if (String.Equals(LayoutPage, name, StringComparison.OrdinalIgnoreCase)) LayoutPage = "All tables";
      SelectedId = null;
      SelectedRelationship = null;
      Invalidate();
    }

    public void ResetView() {
      AutoScrollPosition = new Point(0, 0);
      Zoom = 1.0F;
      SelectedId = null;
      SelectedRelationship = null;
      pageStates.Clear();
      flowPositions.Clear();
      Invalidate();
    }

    public void ZoomIn() {
      SetZoom(Zoom * 1.15F);
    }

    public void ZoomOut() {
      SetZoom(Zoom / 1.15F);
    }

    public Bitmap ExportDataFlowImage(string detailText) {
      if (ViewMode != "dataflow") throw new InvalidOperationException("PNG export is currently available in Data Flow view.");
      LayoutGraph();
      var visible = VisibleNodeIds();
      var rects = positions.Where(p => visible.Contains(p.Key)).Select(p => p.Value).ToList();
      if (rects.Count == 0) throw new InvalidOperationException("There is no visible Data Flow content to export.");

      var bounds = rects[0];
      for (var i = 1; i < rects.Count; i++) bounds = Rectangle.Union(bounds, rects[i]);
      const int padding = 40;
      var flowWidth = Math.Max(320, bounds.Width + padding * 2);
      var flowHeight = Math.Max(220, bounds.Height + padding * 2);
      var hasDetails = !String.IsNullOrWhiteSpace(detailText);
      var detailWidth = hasDetails ? 620 : 0;
      var detailHeight = 0;

      if (hasDetails) {
        using (var measureBitmap = new Bitmap(1, 1))
        using (var measureGraphics = Graphics.FromImage(measureBitmap))
        using (var detailFont = new Font("Consolas", 9.5F))
        using (var detailFormat = new StringFormat()) {
          detailFormat.Trimming = StringTrimming.None;
          var measured = measureGraphics.MeasureString(detailText, detailFont, new SizeF(detailWidth - 56, 1000000F), detailFormat);
          detailHeight = (int)Math.Ceiling(measured.Height) + 80;
        }
      }

      var logicalWidth = flowWidth + detailWidth;
      var logicalHeight = Math.Max(flowHeight, detailHeight);
      var scale = ExportScale(logicalWidth, logicalHeight);
      var pixelWidth = Math.Max(1, (int)Math.Ceiling(logicalWidth * scale));
      var pixelHeight = Math.Max(1, (int)Math.Ceiling(logicalHeight * scale));
      var bitmap = new Bitmap(pixelWidth, pixelHeight, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);

      using (var g = Graphics.FromImage(bitmap)) {
        g.Clear(Theme.Surface);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

        using (var scaleMatrix = new System.Drawing.Drawing2D.Matrix(scale, 0, 0, scale, 0, 0)) {
          g.Transform = scaleMatrix;
          using (var flowBack = new SolidBrush(Theme.Window)) g.FillRectangle(flowBack, 0, 0, flowWidth, logicalHeight);
          DrawExportGrid(g, flowWidth, logicalHeight);
        }

        using (var graphMatrix = new System.Drawing.Drawing2D.Matrix(
          scale, 0, 0, scale,
          scale * (padding - bounds.Left), scale * (padding - bounds.Top))) {
          g.Transform = graphMatrix;
          DrawEdges(g);
          DrawNodes(g);
        }

        if (hasDetails) {
          using (var detailMatrix = new System.Drawing.Drawing2D.Matrix(scale, 0, 0, scale, 0, 0)) {
            g.Transform = detailMatrix;
            DrawExportDetails(g, flowWidth, logicalHeight, detailWidth, detailText);
          }
        }
      }
      return bitmap;
    }

    float ExportScale(int logicalWidth, int logicalHeight) {
      const float preferred = 2F;
      const float maxDimension = 28000F;
      const double maxPixels = 48000000D;
      var scale = preferred;
      scale = Math.Min(scale, maxDimension / Math.Max(1, logicalWidth));
      scale = Math.Min(scale, maxDimension / Math.Max(1, logicalHeight));
      scale = Math.Min(scale, (float)Math.Sqrt(maxPixels / Math.Max(1D, (double)logicalWidth * logicalHeight)));
      return Math.Max(0.35F, scale);
    }

    void DrawExportGrid(Graphics g, int width, int height) {
      using (var brush = new SolidBrush(Color.FromArgb(210, 219, 231))) {
        const int gap = 24;
        for (var x = 12; x < width; x += gap) {
          for (var y = 12; y < height; y += gap) g.FillEllipse(brush, x, y, 1.5F, 1.5F);
        }
      }
    }

    void DrawExportDetails(Graphics g, int left, int height, int width, string detailText) {
      using (var back = new SolidBrush(Theme.Surface)) g.FillRectangle(back, left, 0, width, height);
      using (var divider = new Pen(Theme.Border, 1F)) g.DrawLine(divider, left, 0, left, height);
      using (var detailFont = new Font("Consolas", 9.5F))
      using (var body = new SolidBrush(Color.FromArgb(51, 65, 85)))
      using (var detailFormat = new StringFormat()) {
        detailFormat.Trimming = StringTrimming.None;
        g.DrawString(detailText, detailFont, body, new RectangleF(left + 28, 28, width - 56, height - 56), detailFormat);
      }
    }

    void SetZoom(float value) {
      Zoom = Math.Max(0.35F, Math.Min(2.5F, value));
      Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e) {
      base.OnPaint(e);
      LayoutGraph();
      e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
      e.Graphics.Clear(Theme.Window);
      DrawCanvasGrid(e.Graphics);
      using (var matrix = new System.Drawing.Drawing2D.Matrix(Zoom, 0, 0, Zoom, AutoScrollPosition.X, AutoScrollPosition.Y)) {
        e.Graphics.Transform = matrix;
      }
      DrawEdges(e.Graphics);
      DrawNodes(e.Graphics);
    }

    void DrawCanvasGrid(Graphics g) {
      using (var brush = new SolidBrush(Color.FromArgb(210, 219, 231))) {
        const int gap = 24;
        for (var x = 12; x < ClientSize.Width; x += gap) {
          for (var y = 12; y < ClientSize.Height; y += gap) g.FillEllipse(brush, x, y, 1.5F, 1.5F);
        }
      }
    }

    protected override void OnMouseDown(MouseEventArgs e) {
      Focus();
      if (e.Button == MouseButtons.Left) {
        LayoutGraph();
        var point = ToLogicalPoint(e.Location);
        foreach (var pair in positions) {
          if (pair.Value.Contains(point)) {
            if (ViewMode == "datamodel" && RelatedButtonRect(pair.Value).Contains(point)) break;
            if (ViewMode == "datamodel" && RemoveTableButtonRect(pair.Value).Contains(point)) break;
            dragNodeId = pair.Key;
            dragOffset = new Point(point.X - pair.Value.Left, point.Y - pair.Value.Top);
            dragMoved = false;
            Cursor = Cursors.SizeAll;
            break;
          }
        }
      }
      base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e) {
      if (dragNodeId != null) {
        var point = ToLogicalPoint(e.Location);
        var location = new Point(Math.Max(20, point.X - dragOffset.X), Math.Max(20, point.Y - dragOffset.Y));
        if (ViewMode == "datamodel") CurrentPageState().Positions[dragNodeId] = location;
        else flowPositions[dragNodeId] = location;
        dragMoved = true;
        Invalidate();
      } else {
        UpdateNodeToolTip(ToLogicalPoint(e.Location));
      }
      base.OnMouseMove(e);
    }

    void UpdateNodeToolTip(Point point) {
      string nextId = null;
      foreach (var pair in positions) {
        if (pair.Value.Contains(point)) { nextId = pair.Key; break; }
      }
      if (String.Equals(nextId, hoverNodeId, StringComparison.Ordinal)) return;
      hoverNodeId = nextId;
      var node = String.IsNullOrEmpty(nextId) ? null : Graph.NodeById(nextId);
      nodeToolTip.SetToolTip(this, node == null ? "" : node.Display);
    }

    protected override void OnMouseLeave(EventArgs e) {
      hoverNodeId = null;
      nodeToolTip.SetToolTip(this, "");
      base.OnMouseLeave(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) {
      if (dragNodeId != null) {
        dragNodeId = null;
        Cursor = Cursors.Default;
      }
      base.OnMouseUp(e);
    }

    protected override void OnMouseClick(MouseEventArgs e) {
      if (dragMoved) {
        dragMoved = false;
        base.OnMouseClick(e);
        return;
      }
      var point = ToLogicalPoint(e.Location);
      foreach (var pair in positions) {
        if (pair.Value.Contains(point)) {
          if (ViewMode == "datamodel" && RemoveTableButtonRect(pair.Value).Contains(point)) {
            RemoveTableFromCurrentPage(pair.Key);
            SelectedId = null;
            SelectedRelationship = null;
            Invalidate();
            base.OnMouseClick(e);
            return;
          }
          if (ViewMode == "datamodel" && RelatedButtonRect(pair.Value).Contains(point)) {
            AddRelatedTables(pair.Key);
            SelectedId = pair.Key;
            SelectedRelationship = null;
            Invalidate();
            if (SelectionChanged != null) SelectionChanged(pair.Key);
            base.OnMouseClick(e);
            return;
          }
          SelectedId = pair.Key;
          SelectedRelationship = null;
          Invalidate();
          if (SelectionChanged != null) SelectionChanged(pair.Key);
          base.OnMouseClick(e);
          return;
        }
      }
      if (ViewMode == "datamodel") {
        foreach (var hit in relationshipHits) {
          if (DistanceToPolyline(point, hit.Points) <= 7) {
            SelectedId = null;
            SelectedRelationship = hit.Edge;
            Invalidate();
            if (RelationshipSelectionChanged != null) RelationshipSelectionChanged(hit.Edge);
            base.OnMouseClick(e);
            return;
          }
        }
      }
      base.OnMouseClick(e);
    }

    protected override void OnDragEnter(DragEventArgs drgevent) {
      if (ViewMode == "datamodel" && !CurrentPageState().IsAllTablesPage && drgevent.Data.GetDataPresent(TableBrowser.TableDragFormat)) {
        drgevent.Effect = DragDropEffects.Copy;
      } else {
        drgevent.Effect = DragDropEffects.None;
      }
      base.OnDragEnter(drgevent);
    }

    protected override void OnDragDrop(DragEventArgs drgevent) {
      if (ViewMode == "datamodel" && !CurrentPageState().IsAllTablesPage) {
        var tableId = drgevent.Data.GetData(TableBrowser.TableDragFormat) as string;
        if (!String.IsNullOrEmpty(tableId)) {
          var point = PointToClient(new Point(drgevent.X, drgevent.Y));
          AddTableToCurrentPage(tableId, ToLogicalPoint(point));
          SelectedId = tableId;
          SelectedRelationship = null;
          Invalidate();
          if (SelectionChanged != null) SelectionChanged(tableId);
        }
      }
      base.OnDragDrop(drgevent);
    }

    protected override void OnMouseWheel(MouseEventArgs e) {
      if ((ModifierKeys & Keys.Control) == Keys.Control) {
        if (e.Delta > 0) ZoomIn();
        else ZoomOut();
        return;
      }
      if ((ModifierKeys & Keys.Shift) == Keys.Shift) {
        ScrollHorizontal(-e.Delta);
        return;
      }
      base.OnMouseWheel(e);
    }

    protected override void WndProc(ref Message m) {
      const int WM_MOUSEHWHEEL = 0x020E;
      if (m.Msg == WM_MOUSEHWHEEL) {
        var delta = (short)((m.WParam.ToInt64() >> 16) & 0xffff);
        ScrollHorizontal(-delta);
        return;
      }
      base.WndProc(ref m);
    }

    void ScrollHorizontal(int delta) {
      var maxX = Math.Max(0, AutoScrollMinSize.Width - ClientSize.Width);
      var currentX = -AutoScrollPosition.X;
      AutoScrollPosition = new Point(Math.Max(0, Math.Min(maxX, currentX + delta)), -AutoScrollPosition.Y);
    }

    Point ToLogicalPoint(Point point) {
      return new Point(
        (int)Math.Round((point.X - AutoScrollPosition.X) / Zoom),
        (int)Math.Round((point.Y - AutoScrollPosition.Y) / Zoom));
    }

    void LayoutGraph() {
      positions.Clear();
      if (ViewMode == "datamodel") {
        LayoutDataModel();
        return;
      }
      var visible = VisibleNodeIds();
      var levels = Graph.Levels(false);
      var visibleNodes = Graph.Nodes.Where(n => visible.Contains(n.Id)).ToList();
      var byLevel = visibleNodes.GroupBy(n => levels.ContainsKey(n.Id) ? levels[n.Id] : 0).OrderBy(g => g.Key);
      var visibleLevels = visibleNodes.Select(n => levels.ContainsKey(n.Id) ? levels[n.Id] : 0).ToList();
      var maxLevel = visibleLevels.Count == 0 ? 1 : Math.Max(1, visibleLevels.Max());
      var leftPad = 40;
      var topPad = 32;
      var rightPad = 100;
      var nodeGap = 40;
      var maxNodeWidth = 420;
      var contentWidth = Math.Max(ClientSize.Width, leftPad + rightPad + ((maxLevel + 1) * maxNodeWidth) + (maxLevel * nodeGap));
      var levelStep = maxLevel == 0 ? 0 : (contentWidth - leftPad - rightPad - maxNodeWidth) / maxLevel;
      var totalHeight = 0;
      var maxRight = 0;
      var maxBottom = 0;
      foreach (var group in byLevel) {
        var nodes = group.OrderBy(n => TypeRank(n.Type)).ThenBy(n => n.Display).ToList();
        var x = leftPad + group.Key * levelStep;
        var y = topPad;
        foreach (var node in nodes) {
          var nodeWidth = DataFlowNodeWidth(node);
          var nodeHeight = DataFlowNodeHeight(node, nodeWidth);
          Point location;
          if (!flowPositions.TryGetValue(node.Id, out location)) location = new Point(x, y);
          var rect = new Rectangle(location.X, location.Y, nodeWidth, nodeHeight);
          positions[node.Id] = rect;
          maxRight = Math.Max(maxRight, rect.Right);
          maxBottom = Math.Max(maxBottom, rect.Bottom);
          y += nodeHeight + 20;
        }
        totalHeight = Math.Max(totalHeight, y + 40);
      }
      SetScrollSize(Math.Max(maxRight + rightPad, ClientSize.Width), Math.Max(Math.Max(totalHeight, maxBottom + 80), ClientSize.Height));
    }

    int DataFlowNodeWidth(Node node) {
      using (var font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold)) {
        var measured = TextRenderer.MeasureText(node == null ? "" : node.Display, font, new Size(Int32.MaxValue, 24), TextFormatFlags.SingleLine).Width + 34;
        return Math.Max(280, Math.Min(420, measured));
      }
    }

    int DataFlowNodeHeight(Node node, int width) {
      using (var font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold)) {
        var measured = TextRenderer.MeasureText(node == null ? "" : node.Display, font, new Size(Int32.MaxValue, 24), TextFormatFlags.SingleLine).Width;
        return measured > width - 28 ? 72 : 58;
      }
    }

    void LayoutDataModel() {
      var tableIds = VisibleDataModelTableIds()
        .OrderBy(id => {
          var node = Graph.NodeById(id);
          return node == null ? id : node.Display;
        })
        .ToList();
      var state = CurrentPageState();
      var width = 220;
      var height = 154;
      var gapX = 70;
      var gapY = 54;
      var cols = Math.Max(1, (int)((ClientSize.Width / Zoom) - 60) / (width + gapX));
      var maxRight = 0;
      var maxBottom = 0;
      for (var i = 0; i < tableIds.Count; i++) {
        var tableId = tableIds[i];
        Point location;
        if (!state.Positions.TryGetValue(tableId, out location)) {
          var col = i % cols;
          var row = i / cols;
          location = new Point(40 + col * (width + gapX), 40 + row * (height + gapY));
          state.Positions[tableId] = location;
        }
        var rect = new Rectangle(location.X, location.Y, width, height);
        positions[tableId] = rect;
        maxRight = Math.Max(maxRight, rect.Right);
        maxBottom = Math.Max(maxBottom, rect.Bottom);
      }
      if (tableIds.Count == 0) SetScrollSize(ClientSize.Width, ClientSize.Height);
      else SetScrollSize(Math.Max(maxRight + 80, ClientSize.Width), Math.Max(maxBottom + 80, ClientSize.Height));
    }

    void SetScrollSize(int logicalWidth, int logicalHeight) {
      AutoScrollMinSize = new Size(
        Math.Max((int)Math.Ceiling(logicalWidth * Zoom), ClientSize.Width),
        Math.Max((int)Math.Ceiling(logicalHeight * Zoom), ClientSize.Height));
    }

    HashSet<string> VisibleNodeIds() {
      if (ViewMode == "datamodel") return VisibleDataModelTableIds();
      HashSet<string> visible;
      if (String.IsNullOrEmpty(SelectedId)) {
        visible = new HashSet<string>(Graph.Nodes.Select(n => n.Id));
        return ApplyDataFlowFilter(visible);
      }
      visible = new HashSet<string>();
      visible.Add(SelectedId);
      foreach (var id in Graph.Upstream(SelectedId, false)) visible.Add(id);
      foreach (var id in Graph.Downstream(SelectedId, false)) visible.Add(id);
      var selected = Graph.NodeById(SelectedId);
      if (selected != null && selected.Table.Length > 0) {
        visible.Add("table:" + selected.Table);
        foreach (var sourceId in SourceIdsForTable(selected.Table)) visible.Add(sourceId);
      }
      return ApplyDataFlowFilter(visible);
    }

    HashSet<string> ApplyDataFlowFilter(HashSet<string> ids) {
      return new HashSet<string>(ids.Where(id => {
        var node = Graph.NodeById(id);
        return node != null && FlowFilterAllows(node);
      }));
    }

    bool FlowFilterAllows(Node node) {
      if (node.Type == "source" || node.Type == "source-schema" || node.Type == "source-table") return ShowSources;
      if (node.Type == "table") return ShowTables;
      if (node.Type == "column") return ShowColumns;
      if (node.Type == "measure") return ShowMeasures;
      if (node.Type == "page") return ShowPages;
      return true;
    }

    HashSet<string> VisibleDataModelTableIds() {
      var state = CurrentPageState();
      if (state.IsAllTablesPage) return new HashSet<string>(Graph.Nodes.Where(n => n.Type == "table").Select(n => n.Id));
      return new HashSet<string>(state.TableIds.Where(id => Graph.NodeById(id) != null));
    }

    LayoutPageState CurrentPageState() {
      return PageState(LayoutPage);
    }

    LayoutPageState PageState(string name) {
      var key = String.IsNullOrEmpty(name) ? "All tables" : name;
      LayoutPageState state;
      if (!pageStates.TryGetValue(key, out state)) {
        state = new LayoutPageState();
        state.IsAllTablesPage = String.Equals(key, "All tables", StringComparison.OrdinalIgnoreCase);
        pageStates[key] = state;
      }
      return state;
    }

    void AddTableToCurrentPage(string tableId, Point location) {
      var state = CurrentPageState();
      if (state.IsAllTablesPage) return;
      state.TableIds.Add(tableId);
      state.Positions[tableId] = new Point(Math.Max(24, location.X - 110), Math.Max(24, location.Y - 18));
    }

    void RemoveTableFromCurrentPage(string tableId) {
      var state = CurrentPageState();
      if (state.IsAllTablesPage) return;
      state.TableIds.Remove(tableId);
      state.Positions.Remove(tableId);
    }

    void AddRelatedTables(string anchorTableId) {
      var state = CurrentPageState();
      if (state.IsAllTablesPage) return;
      state.TableIds.Add(anchorTableId);
      if (!positions.ContainsKey(anchorTableId)) LayoutGraph();
      Rectangle anchorRect;
      if (!positions.TryGetValue(anchorTableId, out anchorRect)) anchorRect = new Rectangle(80, 80, 220, 154);
      var related = RelatedTableIds(anchorTableId).Where(id => !String.Equals(id, anchorTableId, StringComparison.OrdinalIgnoreCase)).ToList();
      if (related.Count == 0) return;
      var radius = 290;
      for (var i = 0; i < related.Count; i++) {
        var tableId = related[i];
        if (state.TableIds.Contains(tableId) && state.Positions.ContainsKey(tableId)) continue;
        state.TableIds.Add(tableId);
        var angle = (-Math.PI / 2) + (2 * Math.PI * i / related.Count);
        var x = anchorRect.Left + anchorRect.Width / 2 + (int)Math.Round(Math.Cos(angle) * radius) - 110;
        var y = anchorRect.Top + anchorRect.Height / 2 + (int)Math.Round(Math.Sin(angle) * radius) - 77;
        state.Positions[tableId] = new Point(Math.Max(24, x), Math.Max(24, y));
      }
    }

    List<string> RelatedTableIds(string tableId) {
      var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var edge in Graph.Edges.Where(e => e.Kind == "relationship")) {
        var fromNode = Graph.NodeById(edge.From);
        var toNode = Graph.NodeById(edge.To);
        if (fromNode == null || toNode == null) continue;
        var fromTableId = "table:" + fromNode.Table;
        var toTableId = "table:" + toNode.Table;
        if (String.Equals(fromTableId, tableId, StringComparison.OrdinalIgnoreCase)) result.Add(toTableId);
        if (String.Equals(toTableId, tableId, StringComparison.OrdinalIgnoreCase)) result.Add(fromTableId);
      }
      return result.OrderBy(id => {
        var node = Graph.NodeById(id);
        return node == null ? id : node.Display;
      }).ToList();
    }

    IEnumerable<string> SourceIdsForTable(string table) {
      var tableId = "table:" + table;
      var ids = new HashSet<string>();
      foreach (var edge in Graph.Edges.Where(e => e.To == tableId)) {
        ids.Add(edge.From);
        foreach (var parent in Graph.Edges.Where(e => e.To == edge.From)) {
          ids.Add(parent.From);
          foreach (var grandParent in Graph.Edges.Where(e => e.To == parent.From)) ids.Add(grandParent.From);
        }
      }
      return ids;
    }

    int TypeRank(string type) {
      if (type == "source") return 0;
      if (type == "source-schema") return 1;
      if (type == "source-table") return 2;
      if (type == "table") return 3;
      if (type == "column") return 4;
      if (type == "measure") return 5;
      if (type == "page") return 6;
      return 9;
    }

    void DrawEdges(Graphics g) {
      var visible = VisibleNodeIds();
      using (var pen = new Pen(Color.FromArgb(148, 163, 184), 1.6F)) {
        if (ViewMode == "datamodel") {
          DrawDataModelEdges(g, pen);
          return;
        }
        foreach (var pair in DataFlowVisibleEdges(visible)) {
          if (!positions.ContainsKey(pair.From) || !positions.ContainsKey(pair.To)) continue;
          var a = positions[pair.From];
          var b = positions[pair.To];
          var start = new Point(a.Right, a.Top + a.Height / 2);
          var end = new Point(b.Left, b.Top + b.Height / 2);
          if (SelectedId != null) {
            pen.Color = Theme.Amber;
            pen.Width = 3F;
          } else {
            pen.Color = Color.FromArgb(148, 163, 184);
            pen.Width = 1.6F;
          }
          g.DrawBezier(pen, start, new Point(start.X + 70, start.Y), new Point(end.X - 70, end.Y), end);
          DrawArrow(g, pen.Color, start, end);
        }
      }
    }

    List<VisibleEdge> DataFlowVisibleEdges(HashSet<string> visible) {
      var result = new List<VisibleEdge>();
      var seen = new HashSet<string>();
      foreach (var from in visible) {
        foreach (var to in VisibleDownstreamTargets(from, visible)) {
          if (from == to) continue;
          var key = from + "->" + to;
          if (seen.Contains(key)) continue;
          seen.Add(key);
          result.Add(new VisibleEdge { From = from, To = to });
        }
      }
      return result;
    }

    List<string> VisibleDownstreamTargets(string from, HashSet<string> visible) {
      var result = new List<string>();
      var queue = new Queue<string>();
      var seen = new HashSet<string>();
      foreach (var edge in Graph.Edges.Where(e => e.From == from && e.Kind != "relationship")) queue.Enqueue(edge.To);
      while (queue.Count > 0) {
        var id = queue.Dequeue();
        if (seen.Contains(id)) continue;
        seen.Add(id);
        if (visible.Contains(id)) {
          var target = Graph.NodeById(id);
          if (target == null || target.Type != "page" || IsExplicitPageUsageEdge(from, id)) result.Add(id);
          continue;
        }
        foreach (var edge in Graph.Edges.Where(e => e.From == id && e.Kind != "relationship")) queue.Enqueue(edge.To);
      }
      return result;
    }

    bool IsExplicitPageUsageEdge(string from, string to) {
      var source = Graph.NodeById(from);
      if (source == null || source.Type != "measure") return false;
      return Graph.Edges.Any(e => e.From == from && e.To == to && (e.Kind == "page-usage" || e.Kind == "report-filter"));
    }

    void DrawDataModelEdges(Graphics g, Pen pen) {
      relationshipHits.Clear();
      relationshipOverlays.Clear();
      var pairCounts = new Dictionary<string, int>();
      var visibleTables = VisibleDataModelTableIds();
      var visibleRelationships = Graph.Edges
        .Where(e => e.Kind == "relationship")
        .Select(e => new RelationshipLayout { Edge = e, FromNode = Graph.NodeById(e.From), ToNode = Graph.NodeById(e.To) })
        .Where(r => r.FromNode != null && r.ToNode != null && r.FromNode.Table.Length > 0 && r.ToNode.Table.Length > 0)
        .Where(r => visibleTables.Contains("table:" + r.FromNode.Table) && visibleTables.Contains("table:" + r.ToNode.Table))
        .ToList();
      var endpointTotals = EndpointSideCounts(visibleRelationships);
      var endpointIndexes = new Dictionary<string, int>();
      foreach (var item in visibleRelationships) {
        var edge = item.Edge;
        var fromNode = item.FromNode;
        var toNode = item.ToNode;
        var fromId = "table:" + fromNode.Table;
        var toId = "table:" + toNode.Table;
        if (fromId == toId || !positions.ContainsKey(fromId) || !positions.ContainsKey(toId)) continue;
        var key = String.Compare(fromId, toId, StringComparison.OrdinalIgnoreCase) <= 0 ? fromId + "|" + toId : toId + "|" + fromId;
        var index = pairCounts.ContainsKey(key) ? pairCounts[key] : 0;
        pairCounts[key] = index + 1;
        var a = positions[fromId];
        var b = positions[toId];
        var fromSide = EdgeSide(a, b);
        var toSide = EdgeSide(b, a);
        var rawStart = EdgePort(a, fromSide, NextEndpointIndex(endpointIndexes, fromId, fromSide), endpointTotals[EndpointKey(fromId, fromSide)]);
        var rawEnd = EdgePort(b, toSide, NextEndpointIndex(endpointIndexes, toId, toSide), endpointTotals[EndpointKey(toId, toSide)]);
        var start = rawStart;
        var end = rawEnd;
        var points = OrthogonalRoute(start, end, fromId, toId);
        var selected = SelectedRelationship != null && SelectedRelationship.Id == edge.Id;
        pen.Color = selected ? Color.FromArgb(224, 142, 30) : Color.FromArgb(92, 106, 128);
        pen.Width = selected ? 3.2F : 1.8F;
        DrawPolyline(g, pen, points);
        DrawRelationshipDirection(g, pen.Color, points, edge.Direction, edge.FromCardinality, edge.ToCardinality);
        relationshipOverlays.Add(new RelationshipOverlay {
          FromMarker = edge.FromCardinality.Length > 0 ? edge.FromCardinality : "*",
          ToMarker = edge.ToCardinality.Length > 0 ? edge.ToCardinality : "1",
          FromPoint = start,
          ToPoint = end
        });
        relationshipHits.Add(new RelationshipHit { Edge = edge, Points = points });
      }
    }

    Dictionary<string, int> EndpointSideCounts(List<RelationshipLayout> relationships) {
      var counts = new Dictionary<string, int>();
      foreach (var item in relationships) {
        var fromId = "table:" + item.FromNode.Table;
        var toId = "table:" + item.ToNode.Table;
        if (fromId == toId || !positions.ContainsKey(fromId) || !positions.ContainsKey(toId)) continue;
        IncrementEndpointCount(counts, fromId, EdgeSide(positions[fromId], positions[toId]));
        IncrementEndpointCount(counts, toId, EdgeSide(positions[toId], positions[fromId]));
      }
      return counts;
    }

    void IncrementEndpointCount(Dictionary<string, int> counts, string tableId, string side) {
      var key = EndpointKey(tableId, side);
      counts[key] = counts.ContainsKey(key) ? counts[key] + 1 : 1;
    }

    int NextEndpointIndex(Dictionary<string, int> indexes, string tableId, string side) {
      var key = EndpointKey(tableId, side);
      var value = indexes.ContainsKey(key) ? indexes[key] : 0;
      indexes[key] = value + 1;
      return value;
    }

    string EndpointKey(string tableId, string side) {
      return tableId + "|" + side;
    }

    string EdgeSide(Rectangle source, Rectangle target) {
      var sourceCenter = new Point(source.Left + source.Width / 2, source.Top + source.Height / 2);
      var targetCenter = new Point(target.Left + target.Width / 2, target.Top + target.Height / 2);
      var dx = targetCenter.X - sourceCenter.X;
      var dy = targetCenter.Y - sourceCenter.Y;
      if (Math.Abs(dx) >= Math.Abs(dy)) return dx >= 0 ? "right" : "left";
      return dy >= 0 ? "bottom" : "top";
    }

    Point EdgePort(Rectangle source, string side, int index, int total) {
      total = Math.Max(1, total);
      if (side == "left" || side == "right") {
        var y = source.Top + ((index + 1) * source.Height / (total + 1));
        return new Point(side == "right" ? source.Right : source.Left, y);
      }
      var x = source.Left + ((index + 1) * source.Width / (total + 1));
      return new Point(x, side == "bottom" ? source.Bottom : source.Top);
    }

    List<Point> OrthogonalRoute(Point start, Point end, string fromId, string toId) {
      var candidates = new List<List<Point>>();
      var midX = start.X + (end.X - start.X) / 2;
      var midY = start.Y + (end.Y - start.Y) / 2;
      var xValues = new[] { midX, start.X - 90, start.X + 90, end.X - 90, end.X + 90, midX - 140, midX + 140 };
      var yValues = new[] { midY, start.Y - 70, start.Y + 70, end.Y - 70, end.Y + 70, midY - 130, midY + 130 };
      foreach (var x in xValues) candidates.Add(new List<Point> { start, new Point(x, start.Y), new Point(x, end.Y), end });
      foreach (var y in yValues) candidates.Add(new List<Point> { start, new Point(start.X, y), new Point(end.X, y), end });
      return candidates.OrderBy(route => RouteIntersectionScore(route, fromId, toId)).ThenBy(RouteLength).First();
    }

    int RouteIntersectionScore(List<Point> points, string fromId, string toId) {
      var score = 0;
      foreach (var pair in positions) {
        if (String.Equals(pair.Key, fromId, StringComparison.OrdinalIgnoreCase) || String.Equals(pair.Key, toId, StringComparison.OrdinalIgnoreCase)) continue;
        var rect = Rectangle.Inflate(pair.Value, 8, 8);
        for (var i = 0; i < points.Count - 1; i++) {
          if (SegmentIntersectsRect(points[i], points[i + 1], rect)) score++;
        }
      }
      return score;
    }

    int RouteLength(List<Point> points) {
      var length = 0;
      for (var i = 0; i < points.Count - 1; i++) length += Math.Abs(points[i + 1].X - points[i].X) + Math.Abs(points[i + 1].Y - points[i].Y);
      return length;
    }

    bool SegmentIntersectsRect(Point a, Point b, Rectangle rect) {
      if (rect.Contains(a) || rect.Contains(b)) return true;
      if (a.X == b.X) {
        var minY = Math.Min(a.Y, b.Y);
        var maxY = Math.Max(a.Y, b.Y);
        return a.X >= rect.Left && a.X <= rect.Right && maxY >= rect.Top && minY <= rect.Bottom;
      }
      if (a.Y == b.Y) {
        var minX = Math.Min(a.X, b.X);
        var maxX = Math.Max(a.X, b.X);
        return a.Y >= rect.Top && a.Y <= rect.Bottom && maxX >= rect.Left && minX <= rect.Right;
      }
      return false;
    }

    void DrawPolyline(Graphics g, Pen pen, List<Point> points) {
      for (var i = 0; i < points.Count - 1; i++) g.DrawLine(pen, points[i], points[i + 1]);
    }

    Point OffsetPoint(Point point, Point other, int distance) {
      if (distance == 0) return point;
      var dx = other.X - point.X;
      var dy = other.Y - point.Y;
      var len = Math.Sqrt(dx * dx + dy * dy);
      if (len < 1) return point;
      return new Point((int)Math.Round(point.X - dy / len * distance), (int)Math.Round(point.Y + dx / len * distance));
    }

    void DrawRelationshipDirection(Graphics g, Color color, List<Point> points, string direction, string fromCardinality, string toCardinality) {
      var both = (direction ?? "").IndexOf("both", StringComparison.OrdinalIgnoreCase) >= 0;
      if (!both && fromCardinality == "*" && toCardinality == "1") DrawArrowOnRoute(g, color, Reversed(points), 0.52F);
      else DrawArrowOnRoute(g, color, points, 0.52F);
      if (both) DrawArrowOnRoute(g, color, Reversed(points), 0.48F);
    }

    List<Point> Reversed(List<Point> points) {
      var copy = new List<Point>(points);
      copy.Reverse();
      return copy;
    }

    void DrawArrowOnRoute(Graphics g, Color color, List<Point> points, float routeFraction) {
      var total = Math.Max(1, RouteLength(points));
      var target = total * routeFraction;
      var walked = 0;
      for (var i = 0; i < points.Count - 1; i++) {
        var start = points[i];
        var end = points[i + 1];
        var length = Math.Abs(end.X - start.X) + Math.Abs(end.Y - start.Y);
        if (length == 0) continue;
        if (walked + length >= target) {
          var t = Math.Max(0.15F, Math.Min(0.85F, (float)((target - walked) / length)));
          var point = Interpolate(start, end, t);
          var before = Interpolate(start, end, Math.Max(0F, t - Math.Min(0.22F, 12F / length)));
          DrawArrow(g, color, before, point);
          return;
        }
        walked += length;
      }
    }

    Point Interpolate(Point start, Point end, float t) {
      return new Point((int)Math.Round(start.X + (end.X - start.X) * t), (int)Math.Round(start.Y + (end.Y - start.Y) * t));
    }

    void DrawRelationshipMarker(Graphics g, string marker, Point point) {
      using (var font = new Font(Font.FontFamily, 9F, FontStyle.Bold))
      using (var brush = new SolidBrush(Color.White))
      using (var back = new SolidBrush(Color.FromArgb(92, 106, 128))) {
        var size = g.MeasureString(marker, font);
        var rect = new RectangleF(point.X - size.Width / 2 - 3, point.Y - size.Height / 2 - 2, size.Width + 6, size.Height + 4);
        g.FillRectangle(back, rect);
        g.DrawString(marker, font, brush, rect.Left + 3, rect.Top + 1);
      }
    }

    void DrawArrow(Graphics g, Color color, Point start, Point end) {
      using (var brush = new SolidBrush(color)) {
        var angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
        var size = 9;
        var left = new Point(
          (int)(end.X - size * Math.Cos(angle - Math.PI / 6)),
          (int)(end.Y - size * Math.Sin(angle - Math.PI / 6)));
        var right = new Point(
          (int)(end.X - size * Math.Cos(angle + Math.PI / 6)),
          (int)(end.Y - size * Math.Sin(angle + Math.PI / 6)));
        var pts = new[] { new Point(end.X, end.Y), left, right };
        g.FillPolygon(brush, pts);
      }
    }

    void DrawNodes(Graphics g) {
      if (ViewMode == "datamodel") {
        DrawDataModelNodes(g);
        DrawRelationshipOverlays(g);
        if (VisibleDataModelTableIds().Count == 0 && !CurrentPageState().IsAllTablesPage) DrawEmptyLayoutHint(g);
        return;
      }
      var visible = VisibleNodeIds();
      foreach (var node in Graph.Nodes.Where(n => visible.Contains(n.Id))) {
        if (!positions.ContainsKey(node.Id)) continue;
        var r = positions[node.Id];
        var selected = node.Id == SelectedId;
        var card = new RectangleF(r.Left, r.Top, r.Width, r.Height);
        using (var shadowPath = Theme.RoundRect(new RectangleF(r.Left + 2, r.Top + 5, r.Width, r.Height), 10F))
        using (var shadow = new SolidBrush(Color.FromArgb(24, 15, 23, 42))) g.FillPath(shadow, shadowPath);
        using (var path = Theme.RoundRect(card, 10F))
        using (var brush = new SolidBrush(NodeColor(node.Type)))
        using (var pen = new Pen(selected ? Theme.Amber : Color.FromArgb(55, 255, 255, 255), selected ? 3F : 1F)) {
          g.FillPath(brush, path);
          g.DrawPath(pen, path);
        }
        using (var brush = new SolidBrush(Color.White)) {
          using (var titleFont = new Font("Segoe UI Semibold", 9F, FontStyle.Bold))
          using (var typeFont = new Font("Segoe UI", 7.5F, FontStyle.Bold))
          using (var softBrush = new SolidBrush(Color.FromArgb(205, 255, 255, 255)))
          using (var format = new StringFormat()) {
            format.Trimming = StringTrimming.EllipsisCharacter;
            format.FormatFlags = StringFormatFlags.LineLimit;
            var titleRect = new RectangleF(r.Left + 14, r.Top + 7, r.Width - 28, r.Height - 29);
            g.DrawString(node.Display, titleFont, brush, titleRect, format);
            g.DrawString(NodeTypeLabel(node.Type), typeFont, softBrush, r.Left + 14, r.Bottom - 19);
          }
        }
      }
    }

    string NodeTypeLabel(string type) {
      if (type == "source") return "DATA SOURCE";
      if (type == "source-schema") return "SOURCE SCHEMA";
      if (type == "source-table") return "SOURCE TABLE";
      if (type == "page") return "REPORT PAGE";
      return type.ToUpperInvariant();
    }

    void DrawDataModelNodes(Graphics g) {
      var visibleTableIds = VisibleDataModelTableIds();
      foreach (var table in Graph.Nodes.Where(n => n.Type == "table" && visibleTableIds.Contains(n.Id))) {
        if (!positions.ContainsKey(table.Id)) continue;
        var r = positions[table.Id];
        var selected = table.Id == SelectedId;
        using (var shadowPath = Theme.RoundRect(new RectangleF(r.Left + 2, r.Top + 5, r.Width, r.Height), 10F))
        using (var shadow = new SolidBrush(Color.FromArgb(22, 15, 23, 42))) g.FillPath(shadow, shadowPath);
        using (var path = Theme.RoundRect(new RectangleF(r.Left, r.Top, r.Width, r.Height), 10F))
        using (var back = new SolidBrush(Theme.Surface))
        using (var border = new Pen(selected ? Theme.Amber : Theme.Border, selected ? 3F : 1F)) {
          g.FillPath(back, path);
          g.DrawPath(border, path);
        }
        using (var header = new SolidBrush(Theme.PrimarySoft)) g.FillRectangle(header, r.Left + 1, r.Top + 1, r.Width - 2, 30);
        using (var titleBrush = new SolidBrush(Theme.Ink))
        using (var fieldBrush = new SolidBrush(Color.FromArgb(71, 85, 105)))
        using (var titleFont = new Font(Font.FontFamily, 8.5F, FontStyle.Bold))
        using (var fieldFont = new Font(Font.FontFamily, 8F)) {
          g.DrawString("▦ " + Trim(table.Display, 21), titleFont, titleBrush, r.Left + 8, r.Top + 6);
          if (!CurrentPageState().IsAllTablesPage) DrawRemoveTableButton(g, r);
          DrawRelatedButton(g, r);
          var fields = Graph.Nodes
            .Where(n => n.Table == table.Table && (n.Type == "column" || n.Type == "measure"))
            .OrderBy(n => n.Type == "column" ? 0 : 1)
            .ThenBy(n => n.Display)
            .Take(6)
            .ToList();
          var y = r.Top + 32;
          foreach (var field in fields) {
            var prefix = field.Type == "measure" ? "∑ " : "  ";
            g.DrawString(prefix + Trim(FieldName(field), 22), fieldFont, fieldBrush, r.Left + 16, y);
            y += 18;
          }
        }
      }
    }

    void DrawRelatedButton(Graphics g, Rectangle rect) {
      var button = RelatedButtonRect(rect);
      using (var back = new SolidBrush(Color.White))
      using (var border = new Pen(Color.FromArgb(170, 170, 170)))
      using (var brush = new SolidBrush(Color.FromArgb(70, 70, 70)))
      using (var font = new Font(Font.FontFamily, 8F, FontStyle.Bold)) {
        g.FillRectangle(back, button);
        g.DrawRectangle(border, button);
        g.DrawString("+", font, brush, button.Left + 4, button.Top + 1);
      }
    }

    void DrawRemoveTableButton(Graphics g, Rectangle rect) {
      var button = RemoveTableButtonRect(rect);
      using (var back = new SolidBrush(Color.White))
      using (var border = new Pen(Color.FromArgb(170, 170, 170)))
      using (var brush = new SolidBrush(Color.FromArgb(80, 80, 80)))
      using (var font = new Font(Font.FontFamily, 7.5F, FontStyle.Bold)) {
        g.FillRectangle(back, button);
        g.DrawRectangle(border, button);
        g.DrawString("x", font, brush, button.Left + 4, button.Top + 1);
      }
    }

    Rectangle RelatedButtonRect(Rectangle tableRect) {
      return new Rectangle(tableRect.Right - 22, tableRect.Top + 4, 16, 16);
    }

    Rectangle RemoveTableButtonRect(Rectangle tableRect) {
      return CurrentPageState().IsAllTablesPage ? Rectangle.Empty : new Rectangle(tableRect.Right - 42, tableRect.Top + 4, 16, 16);
    }

    void DrawRelationshipOverlays(Graphics g) {
      foreach (var overlay in relationshipOverlays) {
        DrawRelationshipMarker(g, overlay.FromMarker, overlay.FromPoint);
        DrawRelationshipMarker(g, overlay.ToMarker, overlay.ToPoint);
      }
    }

    void DrawEmptyLayoutHint(Graphics g) {
      using (var brush = new SolidBrush(Color.FromArgb(120, 120, 120)))
      using (var font = new Font(Font.FontFamily, 10F, FontStyle.Regular)) {
        g.DrawString("Drag tables from the right pane into this layout.", font, brush, 42, 42);
      }
    }

    string FieldName(Node node) {
      var prefix = node.Table + "[";
      if (node.Display.StartsWith(prefix) && node.Display.EndsWith("]")) return node.Display.Substring(prefix.Length, node.Display.Length - prefix.Length - 1);
      return node.Display;
    }

    Color NodeColor(string type) {
      if (type == "source") return Color.FromArgb(15, 118, 110);
      if (type == "source-schema") return Color.FromArgb(13, 148, 136);
      if (type == "source-table") return Color.FromArgb(8, 145, 178);
      if (type == "measure") return Color.FromArgb(37, 99, 235);
      if (type == "column") return Color.FromArgb(22, 163, 74);
      if (type == "table") return Color.FromArgb(124, 58, 237);
      if (type == "page") return Color.FromArgb(219, 39, 119);
      return Color.FromArgb(100, 116, 139);
    }

    string Trim(string value, int max) {
      if (value == null) return "";
      return value.Length <= max ? value : value.Substring(0, max - 1) + "...";
    }

    double DistanceToPolyline(Point point, List<Point> points) {
      if (points == null || points.Count < 2) return Double.MaxValue;
      var best = Double.MaxValue;
      for (var i = 0; i < points.Count - 1; i++) best = Math.Min(best, DistanceToSegment(point, points[i], points[i + 1]));
      return best;
    }

    double DistanceToSegment(Point point, Point start, Point end) {
      var dx = end.X - start.X;
      var dy = end.Y - start.Y;
      if (dx == 0 && dy == 0) return Math.Sqrt(Math.Pow(point.X - start.X, 2) + Math.Pow(point.Y - start.Y, 2));
      var t = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / (double)(dx * dx + dy * dy);
      t = Math.Max(0, Math.Min(1, t));
      var x = start.X + t * dx;
      var y = start.Y + t * dy;
      return Math.Sqrt(Math.Pow(point.X - x, 2) + Math.Pow(point.Y - y, 2));
    }
  }

  public class LayoutPageState {
    public bool IsAllTablesPage;
    public readonly Dictionary<string, Point> Positions = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
    public readonly HashSet<string> TableIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  }

  public class RelationshipHit {
    public Edge Edge;
    public List<Point> Points;
  }

  public class RelationshipOverlay {
    public string FromMarker;
    public string ToMarker;
    public Point FromPoint;
    public Point ToPoint;
  }

  public class RelationshipLayout {
    public Edge Edge;
    public Node FromNode;
    public Node ToNode;
  }

  public class VisibleEdge {
    public string From;
    public string To;
  }

  public class ReportLoadResult {
    public int Reports;
    public int Pages;
    public int MeasureLinks;
    public int UnresolvedMeasures;
  }

  public class ReportMeasureRef {
    public string Table = "";
    public string Measure = "";
  }

  public static class ReportParser {
    static readonly JavaScriptSerializer Json = CreateSerializer();

    static JavaScriptSerializer CreateSerializer() {
      var serializer = new JavaScriptSerializer();
      serializer.MaxJsonLength = Int32.MaxValue;
      serializer.RecursionLimit = 512;
      return serializer;
    }

    public static ReportLoadResult AttachReportPages(Graph graph, string tablesRoot) {
      var result = new ReportLoadResult();
      if (graph == null || String.IsNullOrEmpty(tablesRoot)) return result;
      var semanticRoot = FindSemanticModelRoot(tablesRoot);
      if (semanticRoot == null || semanticRoot.Parent == null) return result;
      var reportRoots = FindReportRoots(semanticRoot);
      var qualifyPageNames = reportRoots.Count > 1;
      foreach (var reportRoot in reportRoots) {
        var definition = System.IO.Path.Combine(reportRoot.FullName, "definition");
        var pages = System.IO.Path.Combine(definition, "pages");
        var reportName = ReportName(reportRoot.Name);
        var parsed = false;
        if (Directory.Exists(pages)) {
          ParseEnhancedReport(graph, reportRoot, definition, pages, reportName, qualifyPageNames, result);
          parsed = true;
        } else {
          var legacy = System.IO.Path.Combine(reportRoot.FullName, "report.json");
          if (File.Exists(legacy)) {
            ParseLegacyReport(graph, reportRoot, legacy, reportName, qualifyPageNames, result);
            parsed = true;
          }
        }
        if (parsed) result.Reports++;
      }
      return result;
    }

    static DirectoryInfo FindSemanticModelRoot(string tablesRoot) {
      var current = new DirectoryInfo(tablesRoot);
      while (current != null) {
        if (current.Name.EndsWith(".SemanticModel", StringComparison.OrdinalIgnoreCase)) return current;
        current = current.Parent;
      }
      var tables = new DirectoryInfo(tablesRoot);
      if (tables.Name.Equals("tables", StringComparison.OrdinalIgnoreCase) && tables.Parent != null && tables.Parent.Name.Equals("definition", StringComparison.OrdinalIgnoreCase)) return tables.Parent.Parent;
      return null;
    }

    static List<DirectoryInfo> FindReportRoots(DirectoryInfo semanticRoot) {
      var result = new List<DirectoryInfo>();
      if (semanticRoot == null || semanticRoot.Parent == null) return result;
      var stem = semanticRoot.Name.EndsWith(".SemanticModel", StringComparison.OrdinalIgnoreCase)
        ? semanticRoot.Name.Substring(0, semanticRoot.Name.Length - ".SemanticModel".Length)
        : semanticRoot.Name;
      foreach (var path in Directory.GetDirectories(semanticRoot.Parent.FullName, "*.Report", SearchOption.TopDirectoryOnly)) {
        var report = new DirectoryInfo(path);
        var sameStem = report.Name.Equals(stem + ".Report", StringComparison.OrdinalIgnoreCase);
        if (sameStem || ReferencesSemanticModel(report, semanticRoot)) result.Add(report);
      }
      return result.OrderBy(r => r.Name).ToList();
    }

    static bool ReferencesSemanticModel(DirectoryInfo reportRoot, DirectoryInfo semanticRoot) {
      var definition = System.IO.Path.Combine(reportRoot.FullName, "definition.pbir");
      if (!File.Exists(definition)) return false;
      try {
        var root = AsDictionary(Json.DeserializeObject(File.ReadAllText(definition)));
        var dataset = AsDictionary(Get(root, "datasetReference"));
        var byPath = AsDictionary(Get(dataset, "byPath"));
        var path = StringValue(Get(byPath, "path"));
        if (path.Length == 0) return false;
        var resolved = System.IO.Path.GetFullPath(System.IO.Path.Combine(reportRoot.FullName, path.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        return String.Equals(resolved.TrimEnd('\\'), semanticRoot.FullName.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
      } catch {
        return false;
      }
    }

    static void ParseEnhancedReport(Graph graph, DirectoryInfo reportRoot, string definition, string pagesRoot, string reportName, bool qualify, ReportLoadResult result) {
      foreach (var pageFolder in Directory.GetDirectories(pagesRoot, "*", SearchOption.TopDirectoryOnly).OrderBy(p => p)) {
        var pageFile = System.IO.Path.Combine(pageFolder, "page.json");
        if (!File.Exists(pageFile)) continue;
        object pageObject = DeserializeLoose(File.ReadAllText(pageFile));
        var page = AsDictionary(pageObject);
        var internalName = StringValue(Get(page, "name"));
        if (internalName.Length == 0) internalName = new DirectoryInfo(pageFolder).Name;
        var pageName = StringValue(Get(page, "displayName"));
        if (pageName.Length == 0) pageName = internalName;
        var visibility = StringValue(Get(page, "visibility"));
        var hidden = visibility.IndexOf("hidden", StringComparison.OrdinalIgnoreCase) >= 0;
        var refs = new List<ReportMeasureRef>();
        CollectMeasureReferences(pageObject, refs);
        var visuals = System.IO.Path.Combine(pageFolder, "visuals");
        if (Directory.Exists(visuals)) {
          foreach (var visualFile in Directory.GetFiles(visuals, "visual.json", SearchOption.AllDirectories)) {
            try { CollectMeasureReferences(DeserializeLoose(File.ReadAllText(visualFile)), refs); } catch { }
          }
        }
        AddPage(graph, reportRoot, reportName, internalName, pageName, hidden, pageFile, refs, qualify, "page-usage", result);
      }

      var reportFile = System.IO.Path.Combine(definition, "report.json");
      if (File.Exists(reportFile)) {
        try {
          var root = AsDictionary(DeserializeLoose(File.ReadAllText(reportFile)));
          var filterObject = Get(root, "filterConfig") ?? Get(root, "filters");
          var refs = new List<ReportMeasureRef>();
          if (filterObject != null) CollectMeasureReferences(filterObject, refs);
          if (refs.Count > 0) AddPage(graph, reportRoot, reportName, "report-level-filters", "Report-level filters", false, reportFile, refs, qualify, "report-filter", result);
        } catch { }
      }
    }

    static void ParseLegacyReport(Graph graph, DirectoryInfo reportRoot, string reportFile, string reportName, bool qualify, ReportLoadResult result) {
      var rootObject = DeserializeLoose(File.ReadAllText(reportFile));
      var root = AsDictionary(rootObject);
      foreach (var sectionObject in AsEnumerable(Get(root, "sections"))) {
        var section = AsDictionary(sectionObject);
        var internalName = StringValue(Get(section, "name"));
        var pageName = StringValue(Get(section, "displayName"));
        if (pageName.Length == 0) pageName = internalName;
        var hiddenValue = StringValue(Get(section, "visibility"));
        var hidden = hiddenValue == "1" || hiddenValue.IndexOf("hidden", StringComparison.OrdinalIgnoreCase) >= 0;
        var refs = new List<ReportMeasureRef>();
        CollectMeasureReferences(sectionObject, refs);
        AddPage(graph, reportRoot, reportName, internalName, pageName, hidden, reportFile, refs, qualify, "page-usage", result);
      }
      var filters = Get(root, "filters") ?? Get(root, "filterConfig");
      if (filters != null) {
        var refs = new List<ReportMeasureRef>();
        CollectMeasureReferences(filters, refs);
        if (refs.Count > 0) AddPage(graph, reportRoot, reportName, "report-level-filters", "Report-level filters", false, reportFile, refs, qualify, "report-filter", result);
      }
    }

    static void AddPage(Graph graph, DirectoryInfo reportRoot, string reportName, string internalName, string pageName, bool hidden, string path, List<ReportMeasureRef> refs, bool qualify, string edgeKind, ReportLoadResult result) {
      var id = "page:" + SafeId(reportRoot.Name) + ":" + SafeId(internalName);
      var display = qualify ? reportName + " / " + pageName : pageName;
      if (hidden) display += "  (hidden)";
      var expression = edgeKind == "report-filter" ? "Scope: Applies across the report" : "Visibility: " + (hidden ? "Hidden" : "Visible");
      graph.AddNode(new Node(id, display, "page", reportName, expression, path));
      result.Pages++;
      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var reference in refs) {
        var measure = ResolveMeasure(graph, reference);
        if (measure == null) { result.UnresolvedMeasures++; continue; }
        if (!seen.Add(measure.Id)) continue;
        graph.AddEdge(measure.Id, id, edgeKind);
        result.MeasureLinks++;
      }
    }

    static Node ResolveMeasure(Graph graph, ReportMeasureRef reference) {
      if (reference == null || reference.Measure.Length == 0) return null;
      var candidates = graph.Nodes.Where(n => n.Type == "measure" && MeasureName(n).Equals(reference.Measure, StringComparison.OrdinalIgnoreCase)).ToList();
      if (reference.Table.Length > 0) {
        var exact = candidates.FirstOrDefault(n => n.Table.Equals(reference.Table, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
      }
      return candidates.Count == 1 ? candidates[0] : null;
    }

    static string MeasureName(Node node) {
      if (node == null) return "";
      var prefix = node.Table + "[";
      if (node.Display.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && node.Display.EndsWith("]")) return node.Display.Substring(prefix.Length, node.Display.Length - prefix.Length - 1);
      return node.Display;
    }

    static void CollectMeasureReferences(object root, List<ReportMeasureRef> result) {
      var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      CollectAliases(root, aliases, 0);
      CollectMeasures(root, aliases, result, 0);
    }

    static void CollectAliases(object value, Dictionary<string, string> aliases, int depth) {
      if (value == null || depth > 160) return;
      var dictionary = AsDictionary(value);
      if (dictionary != null) {
        var from = Get(dictionary, "From");
        if (from != null) {
          foreach (var sourceObject in AsEnumerable(from)) {
            var source = AsDictionary(sourceObject);
            var name = StringValue(Get(source, "Name"));
            var entity = StringValue(Get(source, "Entity"));
            if (name.Length > 0 && entity.Length > 0) aliases[name] = entity;
          }
        }
        foreach (var item in dictionary.Values) CollectAliases(item, aliases, depth + 1);
        return;
      }
      var text = value as string;
      object nested;
      if (TryDeserializeNested(text, out nested)) CollectAliases(nested, aliases, depth + 1);
      else if (!(value is string)) foreach (var item in AsEnumerable(value)) CollectAliases(item, aliases, depth + 1);
    }

    static void CollectMeasures(object value, Dictionary<string, string> aliases, List<ReportMeasureRef> result, int depth) {
      if (value == null || depth > 160) return;
      var dictionary = AsDictionary(value);
      if (dictionary != null) {
        var measureObject = Get(dictionary, "Measure");
        var measure = AsDictionary(measureObject);
        if (measure != null) {
          var property = StringValue(Get(measure, "Property"));
          if (property.Length > 0) {
            var table = ResolveSourceTable(Get(measure, "Expression"), aliases);
            if (!result.Any(r => r.Table.Equals(table, StringComparison.OrdinalIgnoreCase) && r.Measure.Equals(property, StringComparison.OrdinalIgnoreCase))) result.Add(new ReportMeasureRef { Table = table, Measure = property });
          }
        }
        foreach (var item in dictionary.Values) CollectMeasures(item, aliases, result, depth + 1);
        return;
      }
      var text = value as string;
      object nested;
      if (TryDeserializeNested(text, out nested)) CollectMeasures(nested, aliases, result, depth + 1);
      else if (!(value is string)) foreach (var item in AsEnumerable(value)) CollectMeasures(item, aliases, result, depth + 1);
    }

    static string ResolveSourceTable(object expression, Dictionary<string, string> aliases) {
      var dictionary = AsDictionary(expression);
      if (dictionary == null) return "";
      var sourceRef = AsDictionary(Get(dictionary, "SourceRef"));
      if (sourceRef != null) {
        var entity = StringValue(Get(sourceRef, "Entity"));
        if (entity.Length > 0) return entity;
        var source = StringValue(Get(sourceRef, "Source"));
        if (source.Length > 0 && aliases.ContainsKey(source)) return aliases[source];
        if (source.Length > 0) return source;
      }
      foreach (var value in dictionary.Values) {
        var resolved = ResolveSourceTable(value, aliases);
        if (resolved.Length > 0) return resolved;
      }
      return "";
    }

    static object DeserializeLoose(string text) {
      return Json.DeserializeObject(text);
    }

    static bool TryDeserializeNested(string text, out object value) {
      value = null;
      if (String.IsNullOrWhiteSpace(text)) return false;
      var trimmed = text.Trim();
      if (!(trimmed.StartsWith("{") && trimmed.EndsWith("}")) && !(trimmed.StartsWith("[") && trimmed.EndsWith("]"))) return false;
      try { value = Json.DeserializeObject(trimmed); return value != null; } catch { return false; }
    }

    static IDictionary<string, object> AsDictionary(object value) {
      return value as IDictionary<string, object>;
    }

    static IEnumerable AsEnumerable(object value) {
      var enumerable = value as IEnumerable;
      return enumerable ?? new object[0];
    }

    static object Get(IDictionary<string, object> dictionary, string key) {
      if (dictionary == null) return null;
      object value;
      if (dictionary.TryGetValue(key, out value)) return value;
      foreach (var pair in dictionary) if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return pair.Value;
      return null;
    }

    static string StringValue(object value) {
      return value == null ? "" : Convert.ToString(value) ?? "";
    }

    static string SafeId(string value) {
      return Regex.Replace(value ?? "", @"[^A-Za-z0-9_-]+", "_");
    }

    static string ReportName(string folderName) {
      return folderName.EndsWith(".Report", StringComparison.OrdinalIgnoreCase) ? folderName.Substring(0, folderName.Length - ".Report".Length) : folderName;
    }
  }

  public class TmdlParser {
    public static Graph Parse(List<TmdlFile> files) {
      var graph = new Graph();
      var measures = new Dictionary<string, Node>();
      var measureByName = new Dictionary<string, List<Node>>();
      var columns = new Dictionary<string, Node>();
      var tables = new Dictionary<string, Node>();
      var sources = new Dictionary<string, Node>();
      var tableSources = new Dictionary<string, List<Node>>();

      foreach (var file in files) {
        var table = InferTableName(file.Path, file.Content);
        if (table.Length == 0) continue;
        var partitions = ExtractPartitions(file.Content);
        var sourceText = String.Join(Environment.NewLine + Environment.NewLine, partitions.Select(p => p.Expression).Where(x => x.Length > 0).ToArray());
        tables[table] = new Node("table:" + table, table, "table", table, sourceText, file.Path);
        foreach (var source in ExtractSources(partitions, table, sourceText, file.Path)) {
          var sourceKey = source.Id;
          sources[sourceKey] = source;
          if (!tableSources.ContainsKey(table)) tableSources[table] = new List<Node>();
          tableSources[table].Add(source);
        }
        var sourceColumns = MergeColumns(ExtractNativeQueryColumns(sourceText), ExtractPowerQueryColumns(sourceText));
        foreach (var column in MergeColumns(ExtractColumns(file.Content), sourceColumns)) {
          var key = table + "[" + column + "]";
          columns[key] = new Node("column:" + key, key, "column", table, "", "");
        }
        foreach (var measure in ExtractMeasures(file.Content)) {
          var tableName = measure.Table.Length > 0 ? measure.Table : table;
          var key = tableName + "[" + measure.Name + "]";
          var node = new Node("measure:" + key, key, "measure", tableName, measure.Expression.Trim(), file.Path);
          measures[key] = node;
          if (!measureByName.ContainsKey(measure.Name)) measureByName[measure.Name] = new List<Node>();
          measureByName[measure.Name].Add(node);
        }
      }

      foreach (var node in sources.Values) graph.AddNode(node);
      foreach (var node in tables.Values) graph.AddNode(node);
      foreach (var node in columns.Values) graph.AddNode(node);
      foreach (var node in measures.Values) graph.AddNode(node);

      foreach (var pair in tableSources) {
        var tableId = "table:" + pair.Key;
        var ordered = pair.Value.OrderBy(n => TypeRank(n.Type)).ToList();
        for (var i = 0; i < ordered.Count - 1; i++) graph.AddEdge(ordered[i].Id, ordered[i + 1].Id, ordered[i].Type == "source" ? "catalog-to-source-table" : "source-chain");
        foreach (var source in ordered.Where(n => n.Type == "source-table")) graph.AddEdge(source.Id, tableId, "source-to-table");
        if (!ordered.Any(n => n.Type == "source-table")) foreach (var source in ordered) graph.AddEdge(source.Id, tableId, "source-to-table");
      }
      foreach (var column in columns.Values) {
        graph.AddEdge("table:" + column.Table, column.Id, "table-to-column");
      }
      foreach (var relationship in ExtractRelationships(files.Select(f => f.Content))) {
        var fromId = "column:" + relationship.From;
        var toId = "column:" + relationship.To;
        if (graph.NodeById(fromId) == null) graph.AddNode(new Node(fromId, relationship.From, "column", TableFromQualified(relationship.From), "", ""));
        if (graph.NodeById(toId) == null) graph.AddNode(new Node(toId, relationship.To, "column", TableFromQualified(relationship.To), "", ""));
        graph.AddEdge(fromId, toId, "relationship", relationship.FromCardinality, relationship.ToCardinality, relationship.CrossFilteringBehavior, relationship.IsActive);
      }

      foreach (var measure in measures.Values) {
        var refs = ExtractReferences(measure.Expression);
        foreach (var col in refs.Columns) {
          var id = "column:" + col;
          if (graph.NodeById(id) == null) graph.AddNode(new Node(id, col, "column", TableFromQualified(col), "", ""));
          graph.AddEdge(id, measure.Id, "column-to-measure");
        }
        foreach (var refMeasure in refs.Measures) {
          List<Node> targets;
          if (!measureByName.TryGetValue(refMeasure, out targets)) {
            targets = new List<Node> { new Node("measure:unknown[" + refMeasure + "]", "unknown[" + refMeasure + "]", "measure", "unknown", "", "") };
          }
          foreach (var target in targets) {
            graph.AddNode(target);
            graph.AddEdge(target.Id, measure.Id, "measure-to-measure");
          }
        }
      }

      return graph;
    }

    static string InferTableName(string path, string text) {
      var match = Regex.Match(text, @"^\s*(?:createOrReplace\s+)?table\s+(.+)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
      if (match.Success) return Clean(match.Groups[1].Value);
      var name = System.IO.Path.GetFileNameWithoutExtension(path);
      return name ?? "";
    }

    static List<string> ExtractColumns(string text) {
      var result = new List<string>();
      foreach (Match m in Regex.Matches(text, @"^\s*column\s+(.+)\s*$", RegexOptions.Multiline)) {
        result.Add(Clean(m.Groups[1].Value));
      }
      return result;
    }

    static List<string> MergeColumns(List<string> modelColumns, List<string> sourceColumns) {
      var result = new List<string>();
      foreach (var column in modelColumns.Concat(sourceColumns)) {
        var clean = Clean(column);
        if (clean.Length > 0 && !result.Contains(clean)) result.Add(clean);
      }
      return result;
    }

    static List<PartitionDef> ExtractPartitions(string text) {
      var lines = text.Replace("\r\n", "\n").Split('\n');
      var result = new List<PartitionDef>();
      for (var i = 0; i < lines.Length; i++) {
        var start = Regex.Match(lines[i], @"^\s*partition\s+(.+?)(?:\s*=\s*\w+)?\s*$");
        if (!start.Success) continue;
        var indent = LeadingSpaces(lines[i]);
        var name = Clean(start.Groups[1].Value);
        var body = new List<string>();
        var j = i + 1;
        while (j < lines.Length) {
          var line = lines[j];
          if (Regex.IsMatch(line, @"^\s*(measure|column|table|relationship|partition|hierarchy|calculationGroup)\b") && LeadingSpaces(line) <= indent) break;
          body.Add(line);
          j++;
        }
        result.Add(new PartitionDef { Name = name, Expression = ExtractPartitionSource(body) });
        i = j - 1;
      }
      return result;
    }

    static string ExtractPartitionSource(List<string> body) {
      var source = new List<string>();
      var collecting = false;
      var sourceIndent = 0;
      foreach (var line in body) {
        if (!collecting) {
          var match = Regex.Match(line, @"^\s*source\s*=\s*(.*)$", RegexOptions.IgnoreCase);
          if (!match.Success) continue;
          collecting = true;
          sourceIndent = LeadingSpaces(line);
          if (match.Groups[1].Value.Trim().Length > 0) source.Add(match.Groups[1].Value);
          continue;
        }
        if (line.Trim().Length > 0 && LeadingSpaces(line) <= sourceIndent && Regex.IsMatch(line, @"^\s*\w+\s*:")) break;
        source.Add(line);
      }
      return String.Join(Environment.NewLine, source.ToArray()).Trim();
    }

    static List<Node> ExtractSources(List<PartitionDef> partitions, string table, string sourceText, string path) {
      var nodes = new List<Node>();
      foreach (var partition in partitions) {
        var native = ExtractNativeQuerySource(partition.Expression, table, path);
        if (native.Count > 0) {
          foreach (var node in native) AddUniqueNode(nodes, node);
          continue;
        }
        var navigation = ExtractNavigationSource(partition.Expression, sourceText, path);
        if (navigation.Count > 0) {
          foreach (var node in navigation) AddUniqueNode(nodes, node);
          continue;
        }
        foreach (var name in ExtractMSourceNames(partition.Expression)) {
          AddUniqueNode(nodes, new Node("source:" + name, name, "source", "", sourceText, path));
        }
      }
      return nodes;
    }

    static List<Node> ExtractNativeQuerySource(string expression, string table, string path) {
      var result = new List<Node>();
      if (String.IsNullOrWhiteSpace(expression) || !Regex.IsMatch(expression, @"Value\.NativeQuery\s*\(", RegexOptions.IgnoreCase)) return result;
      var catalog = Clean(Regex.Match(expression, @"\bCatalog\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase).Groups[1].Value);
      var sql = ExtractNativeQuerySql(expression);
      var sourceTable = ExtractSqlFromTable(sql);
      var schema = SchemaFromSourceTable(sourceTable);
      if (catalog.Length > 0) result.Add(new Node("source:" + catalog, catalog, "source", "", expression.Trim(), path));
      if (catalog.Length > 0 && schema.Length > 0) result.Add(new Node("source-schema:" + catalog + "." + schema, schema, "source-schema", "", expression.Trim(), path));
      if (sourceTable.Length > 0) result.Add(new Node("source-table:" + (catalog.Length > 0 ? catalog + "." : "") + sourceTable, sourceTable, "source-table", "", sql.Length > 0 ? sql : expression.Trim(), path));
      return result;
    }

    static List<Node> ExtractNavigationSource(string expression, string sourceText, string path) {
      var result = new List<Node>();
      if (String.IsNullOrWhiteSpace(expression)) return result;
      var steps = Regex.Matches(expression, @"Name\s*=\s*""([^""]+)""\s*,\s*Kind\s*=\s*""(Database|Schema|Table|View)""", RegexOptions.IgnoreCase)
        .Cast<Match>()
        .Select(m => new SourceStep { Name = Clean(m.Groups[1].Value), Kind = Clean(m.Groups[2].Value).ToLowerInvariant() })
        .ToList();
      if (steps.Count == 0) return result;
      var database = steps.LastOrDefault(s => s.Kind == "database");
      var schema = steps.LastOrDefault(s => s.Kind == "schema");
      var table = steps.LastOrDefault(s => s.Kind == "table" || s.Kind == "view");
      if (database == null || schema == null || table == null) return result;
      var physicalTable = schema.Name + "." + table.Name;
      result.Add(new Node("source:" + database.Name, database.Name, "source", "", sourceText, path));
      result.Add(new Node("source-schema:" + database.Name + "." + schema.Name, schema.Name, "source-schema", "", sourceText, path));
      result.Add(new Node("source-table:" + database.Name + "." + physicalTable, physicalTable, "source-table", "", sourceText, path));
      return result;
    }

    static string SchemaFromSourceTable(string sourceTable) {
      var parts = (sourceTable ?? "").Split('.');
      return parts.Length >= 2 ? parts[parts.Length - 2] : "";
    }

    static string ExtractNativeQuerySql(string expression) {
      var marker = Regex.Match(expression ?? "", @"Value\.NativeQuery\s*\(", RegexOptions.IgnoreCase);
      if (!marker.Success) return "";
      var strings = new List<string>();
      var inString = false;
      var current = "";
      for (var i = marker.Index; i < expression.Length; i++) {
        var ch = expression[i];
        var next = i + 1 < expression.Length ? expression[i + 1] : '\0';
        if (!inString && ch == '"') {
          inString = true;
          current = "";
          continue;
        }
        if (!inString) continue;
        if (ch == '"' && next == '"') {
          current += '"';
          i++;
          continue;
        }
        if (ch == '"') {
          strings.Add(NormalizePowerQuerySql(current));
          inString = false;
          continue;
        }
        current += ch;
      }
      return strings.FirstOrDefault(s => Regex.IsMatch(s, @"\bselect\b[\s\S]+\bfrom\b", RegexOptions.IgnoreCase)) ?? "";
    }

    static string NormalizePowerQuerySql(string sql) {
      return (sql ?? "")
        .Replace("#(lf)", Environment.NewLine)
        .Replace("#(LF)", Environment.NewLine)
        .Replace("#(cr)", "\r")
        .Replace("#(CR)", "\r")
        .Replace("#(tab)", "\t")
        .Replace("#(TAB)", "\t")
        .Trim();
    }

    static string StripSqlComments(string sql) {
      var cleaned = Regex.Replace(sql ?? "", @"/\*[\s\S]*?\*/", " ");
      return Regex.Replace(cleaned, @"--.*$", "", RegexOptions.Multiline);
    }

    static string ExtractSqlFromTable(string sql) {
      var match = Regex.Match(StripSqlComments(sql), @"\bfrom\s+((?:\[[^\]]+\]|""[^""]+""|`[^`]+`|[A-Za-z_][\w$-]*)(?:\s*\.\s*(?:\[[^\]]+\]|""[^""]+""|`[^`]+`|[A-Za-z_][\w$-]*)){0,2})", RegexOptions.IgnoreCase);
      return match.Success ? CleanSqlPath(match.Groups[1].Value) : "";
    }

    static List<string> ExtractNativeQueryColumns(string expression) {
      var sql = ExtractNativeQuerySql(expression);
      if (sql.Length == 0) return new List<string>();
      var cleaned = StripSqlComments(sql);
      var match = Regex.Match(cleaned, @"\bselect\b([\s\S]*?)\bfrom\b", RegexOptions.IgnoreCase);
      if (!match.Success) return new List<string>();
      return SplitTopLevel(match.Groups[1].Value, ',').Select(ColumnNameFromSelectExpression).Where(x => x.Length > 0).Distinct().ToList();
    }

    static List<string> ExtractPowerQueryColumns(string expression) {
      var selected = new List<string>();
      foreach (Match m in Regex.Matches(expression ?? "", @"Table\.SelectColumns\s*\([^\{]*\{([^}]*)\}", RegexOptions.IgnoreCase | RegexOptions.Singleline)) {
        foreach (var name in ExtractQuotedNames(m.Groups[1].Value)) AddUnique(selected, name);
      }
      if (selected.Count == 0) return selected;
      foreach (Match m in Regex.Matches(expression ?? "", @"Table\.RemoveColumns\s*\([^\{]*\{([^}]*)\}", RegexOptions.IgnoreCase | RegexOptions.Singleline)) {
        foreach (var name in ExtractQuotedNames(m.Groups[1].Value)) selected.Remove(name);
      }
      return selected;
    }

    static List<string> ExtractQuotedNames(string value) {
      var result = new List<string>();
      foreach (Match m in Regex.Matches(value ?? "", @"""([^""]+)""")) AddUnique(result, m.Groups[1].Value);
      return result;
    }

    static List<string> SplitTopLevel(string value, char separator) {
      var parts = new List<string>();
      var current = "";
      var depth = 0;
      var quote = '\0';
      foreach (var ch in value ?? "") {
        if (quote != '\0') {
          current += ch;
          if (ch == quote) quote = '\0';
          continue;
        }
        if (ch == '"' || ch == '\'' || ch == '`') {
          quote = ch;
          current += ch;
          continue;
        }
        if (ch == '(') depth++;
        if (ch == ')') depth = Math.Max(0, depth - 1);
        if (ch == separator && depth == 0) {
          parts.Add(current);
          current = "";
          continue;
        }
        current += ch;
      }
      if (current.Trim().Length > 0) parts.Add(current);
      return parts;
    }

    static string ColumnNameFromSelectExpression(string expression) {
      var value = (expression ?? "").Trim();
      if (value.Length == 0 || value == "*") return "";
      var alias = Regex.Match(value, @"\s+as\s+((?:\[[^\]]+\]|""[^""]+""|`[^`]+`|[A-Za-z_][\w$-]*))\s*$", RegexOptions.IgnoreCase);
      if (alias.Success) return CleanSqlIdentifier(alias.Groups[1].Value);
      var trailing = Regex.Match(value, @"(?:^|\.)(\[[^\]]+\]|""[^""]+""|`[^`]+`|[A-Za-z_][\w$-]*)\s*$");
      return trailing.Success ? CleanSqlIdentifier(trailing.Groups[1].Value) : "";
    }

    static string CleanSqlPath(string value) {
      return String.Join(".", (value ?? "").Split('.').Select(CleanSqlIdentifier).ToArray());
    }

    static string CleanSqlIdentifier(string value) {
      var clean = (value ?? "").Trim();
      if (clean.StartsWith("[") && clean.EndsWith("]")) clean = clean.Substring(1, clean.Length - 2);
      if ((clean.StartsWith("\"") && clean.EndsWith("\"")) || (clean.StartsWith("`") && clean.EndsWith("`"))) clean = clean.Substring(1, clean.Length - 2);
      return clean.Trim();
    }

    static List<string> ExtractMSourceNames(string expression) {
      var result = new List<string>();
      if (String.IsNullOrWhiteSpace(expression)) return result;

      var hierarchy = new List<string>();
      foreach (Match m in Regex.Matches(expression, @"Name\s*=\s*""([^""]+)""\s*,\s*Kind\s*=\s*""(Database|Schema|Table|View)""", RegexOptions.IgnoreCase)) {
        hierarchy.Add(m.Groups[1].Value);
      }
      if (hierarchy.Count >= 3) {
        result.Add(hierarchy[hierarchy.Count - 3] + "." + hierarchy[hierarchy.Count - 2] + "." + hierarchy[hierarchy.Count - 1]);
        return result;
      }
      if (hierarchy.Count > 0) {
        result.Add(String.Join(".", hierarchy.ToArray()));
        return result;
      }

      foreach (Match m in Regex.Matches(expression, @"(?:Sql\.Database|PostgreSQL\.Database|MySQL\.Database|Snowflake\.Databases?)\s*\(\s*""([^""]+)""\s*,\s*""([^""]+)""", RegexOptions.IgnoreCase)) {
        AddUnique(result, m.Groups[1].Value + "." + m.Groups[2].Value);
      }
      foreach (Match m in Regex.Matches(expression, @"(?:Databricks\.Catalogs|Databricks\.Contents)\s*\(\s*""([^""]+)""", RegexOptions.IgnoreCase)) {
        AddUnique(result, "Databricks: " + m.Groups[1].Value);
      }
      foreach (Match m in Regex.Matches(expression, @"(?:Odbc\.DataSource|OleDb\.DataSource|Web\.Contents|SharePoint\.Files|SharePoint\.Contents)\s*\(\s*""([^""]+)""", RegexOptions.IgnoreCase)) {
        AddUnique(result, m.Groups[1].Value);
      }
      return result;
    }

    static void AddUnique(List<string> items, string value) {
      if (!String.IsNullOrEmpty(value) && !items.Contains(value)) items.Add(value);
    }

    static void AddUniqueNode(List<Node> items, Node node) {
      if (node != null && !items.Any(n => n.Id == node.Id)) items.Add(node);
    }

    static List<RelationshipDef> ExtractRelationships(IEnumerable<string> contents) {
      var result = new List<RelationshipDef>();
      foreach (var text in contents) {
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
        RelationshipDef current = null;
        foreach (var line in lines) {
          var rel = Regex.Match(line, @"^\s*relationship\s+(.+?)\s*$", RegexOptions.IgnoreCase);
          if (rel.Success) {
            if (current != null && current.From.Length > 0 && current.To.Length > 0) result.Add(current);
            current = new RelationshipDef { Name = Clean(rel.Groups[1].Value), From = "", To = "", FromCardinality = "*", ToCardinality = "1", CrossFilteringBehavior = "", IsActive = true };
            continue;
          }
          if (current == null) continue;
          var from = Regex.Match(line, @"^\s*(?:fromColumn|from)\s*:?\s*(.+?)\s*$", RegexOptions.IgnoreCase);
          var to = Regex.Match(line, @"^\s*(?:toColumn|to)\s*:?\s*(.+?)\s*$", RegexOptions.IgnoreCase);
          if (from.Success) current.From = NormalizeQualifiedColumn(from.Groups[1].Value);
          if (to.Success) current.To = NormalizeQualifiedColumn(to.Groups[1].Value);
          var fromCardinality = Regex.Match(line, @"^\s*fromCardinality\s*:?\s*(.+?)\s*$", RegexOptions.IgnoreCase);
          var toCardinality = Regex.Match(line, @"^\s*toCardinality\s*:?\s*(.+?)\s*$", RegexOptions.IgnoreCase);
          var cardinality = Regex.Match(line, @"^\s*cardinality\s*:?\s*(.+?)\s*$", RegexOptions.IgnoreCase);
          var crossFilter = Regex.Match(line, @"^\s*(?:crossFilteringBehavior|crossFilterDirection)\s*:?\s*(.+?)\s*$", RegexOptions.IgnoreCase);
          var active = Regex.Match(line, @"^\s*(?:isActive|active)\s*:?\s*(.+?)\s*$", RegexOptions.IgnoreCase);
          if (fromCardinality.Success) current.FromCardinality = CardinalityMarker(fromCardinality.Groups[1].Value, "*");
          if (toCardinality.Success) current.ToCardinality = CardinalityMarker(toCardinality.Groups[1].Value, "1");
          if (cardinality.Success) ApplyCardinality(current, cardinality.Groups[1].Value);
          if (crossFilter.Success) current.CrossFilteringBehavior = Clean(crossFilter.Groups[1].Value);
          if (active.Success) current.IsActive = !Regex.IsMatch(Clean(active.Groups[1].Value), @"^(false|0|no)$", RegexOptions.IgnoreCase);
          if (Regex.IsMatch(line, @"^\s*(table|measure|column|partition)\b", RegexOptions.IgnoreCase)) {
            if (current.From.Length > 0 && current.To.Length > 0) result.Add(current);
            current = null;
          }
        }
        if (current != null && current.From.Length > 0 && current.To.Length > 0) result.Add(current);
      }
      return result;
    }

    static void ApplyCardinality(RelationshipDef relationship, string value) {
      var raw = Clean(value).ToLowerInvariant();
      if (raw.Contains("one") && raw.Contains("many")) {
        relationship.FromCardinality = raw.StartsWith("one") ? "1" : "*";
        relationship.ToCardinality = raw.StartsWith("one") ? "*" : "1";
      }
    }

    static string CardinalityMarker(string value, string fallback) {
      var raw = Clean(value).ToLowerInvariant();
      if (raw.Contains("one") || raw == "1") return "1";
      if (raw.Contains("many") || raw == "*") return "*";
      return fallback;
    }

    static string NormalizeQualifiedColumn(string value) {
      var cleaned = Clean(value);
      var match = Regex.Match(cleaned, @"^(?:'([^']+)'|([^\[]+))\s*\[([^\]]+)\]$");
      if (!match.Success) {
        var dot = Regex.Match(cleaned, @"^(?:'([^']+)'|""([^""]+)""|([A-Za-z_][\w .-]*))\s*\.\s*(?:'([^']+)'|""([^""]+)""|([A-Za-z_][\w .-]*))$");
        if (dot.Success) {
          var table = Clean(dot.Groups[1].Success ? dot.Groups[1].Value : dot.Groups[2].Success ? dot.Groups[2].Value : dot.Groups[3].Value);
          var column = Clean(dot.Groups[4].Success ? dot.Groups[4].Value : dot.Groups[5].Success ? dot.Groups[5].Value : dot.Groups[6].Value);
          return table + "[" + column + "]";
        }
        return cleaned;
      }
      return Clean(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) + "[" + Clean(match.Groups[3].Value) + "]";
    }

    static List<MeasureDef> ExtractMeasures(string text) {
      var lines = text.Replace("\r\n", "\n").Split('\n');
      var result = new List<MeasureDef>();
      for (var i = 0; i < lines.Length; i++) {
        var start = Regex.Match(lines[i], @"^\s*measure\s+(.+?)\s*=\s*(.*)$");
        if (!start.Success) continue;
        var indent = LeadingSpaces(lines[i]);
        var lhs = start.Groups[1].Value.Trim();
        var table = "";
        var name = Clean(lhs);
        var qualified = Regex.Match(lhs, @"^(?:'([^']+)'|([^\[]+))?\s*\[([^\]]+)\]$");
        if (qualified.Success) {
          table = Clean(qualified.Groups[1].Success ? qualified.Groups[1].Value : qualified.Groups[2].Value);
          name = Clean(qualified.Groups[3].Value);
        }
        var expr = new List<string>();
        expr.Add(start.Groups[2].Value);
        var j = i + 1;
        while (j < lines.Length) {
          var line = lines[j];
          if (Regex.IsMatch(line, @"^\s*(measure|column|table|relationship|partition|hierarchy|calculationGroup)\b") && LeadingSpaces(line) <= indent) break;
          if (Regex.IsMatch(line, @"^\s+(?!formatString|displayFolder|description|isHidden|lineageTag|summarizeBy|sourceColumn|dataType\b)") || line.Trim().Length == 0) expr.Add(line);
          else if (LeadingSpaces(line) <= indent && line.Trim().Length > 0) break;
          j++;
        }
        result.Add(new MeasureDef { Table = table, Name = name, Expression = String.Join(Environment.NewLine, expr.ToArray()) });
        i = j - 1;
      }
      return result;
    }

    static RefSet ExtractReferences(string expression) {
      var refs = new RefSet();
      var withoutStrings = Regex.Replace(expression ?? "", @"""(?:""""|[^""])*""", "");
      foreach (Match m in Regex.Matches(withoutStrings, @"(?:'([^']+)'|([A-Za-z_][\w .-]*))\s*\[([^\]]+)\]")) {
        var table = Clean(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        var item = Clean(m.Groups[3].Value);
        if (table.Length > 0 && item.Length > 0) refs.Columns.Add(table + "[" + item + "]");
      }
      foreach (Match m in Regex.Matches(withoutStrings, @"(?<![\w'\]])\[([^\]]+)\]")) {
        refs.Measures.Add(Clean(m.Groups[1].Value));
      }
      foreach (var col in refs.Columns.ToList()) {
        var measureName = col.Substring(col.IndexOf('[') + 1).TrimEnd(']');
        refs.Measures.Remove(measureName);
      }
      return refs;
    }

    static string Clean(string value) {
      value = (value ?? "").Trim();
      if ((value.StartsWith("'") && value.EndsWith("'")) || (value.StartsWith("\"") && value.EndsWith("\""))) {
        value = value.Substring(1, value.Length - 2);
      }
      return value.Trim();
    }

    static int TypeRank(string type) {
      if (type == "source") return 0;
      if (type == "source-schema") return 1;
      if (type == "source-table") return 2;
      if (type == "table") return 3;
      if (type == "column") return 4;
      if (type == "measure") return 5;
      if (type == "page") return 6;
      return 9;
    }

    static int LeadingSpaces(string s) {
      var count = 0;
      while (count < s.Length && Char.IsWhiteSpace(s[count])) count++;
      return count;
    }

    static string TableFromQualified(string value) {
      var index = (value ?? "").IndexOf('[');
      return index < 0 ? "" : value.Substring(0, index);
    }
  }

  public class Graph {
    public readonly List<Node> Nodes = new List<Node>();
    public readonly List<Edge> Edges = new List<Edge>();
    readonly Dictionary<string, Node> nodeMap = new Dictionary<string, Node>();
    readonly HashSet<string> edgeIds = new HashSet<string>();

    public void AddNode(Node node) {
      if (node == null || node.Id.Length == 0 || nodeMap.ContainsKey(node.Id)) return;
      nodeMap[node.Id] = node;
      Nodes.Add(node);
    }

    public void AddEdge(string from, string to, string kind) {
      AddEdge(from, to, kind, "", "", "", true);
    }

    public void AddEdge(string from, string to, string kind, string fromCardinality, string toCardinality, string direction) {
      AddEdge(from, to, kind, fromCardinality, toCardinality, direction, true);
    }

    public void AddEdge(string from, string to, string kind, string fromCardinality, string toCardinality, string direction, bool isActive) {
      if (from == to) return;
      var id = from + "->" + to;
      if (edgeIds.Contains(id)) return;
      edgeIds.Add(id);
      Edges.Add(new Edge { Id = id, From = from, To = to, Kind = kind, FromCardinality = fromCardinality ?? "", ToCardinality = toCardinality ?? "", Direction = direction ?? "", IsActive = isActive });
    }

    public Node NodeById(string id) {
      if (String.IsNullOrEmpty(id)) return null;
      Node node;
      return nodeMap.TryGetValue(id, out node) ? node : null;
    }

    public List<string> Upstream(string nodeId) {
      return Upstream(nodeId, true);
    }

    public List<string> Upstream(string nodeId, bool includeRelationships) {
      var seen = new HashSet<string>();
      var result = new List<string>();
      WalkUp(nodeId, seen, result, includeRelationships);
      return result;
    }

    void WalkUp(string id, HashSet<string> seen, List<string> result, bool includeRelationships) {
      foreach (var edge in Edges.Where(e => e.To == id)) {
        if (!includeRelationships && edge.Kind == "relationship") continue;
        if (seen.Contains(edge.From)) continue;
        seen.Add(edge.From);
        result.Add(edge.From);
        WalkUp(edge.From, seen, result, includeRelationships);
      }
    }

    public List<string> Downstream(string nodeId) {
      return Downstream(nodeId, true);
    }

    public List<string> Downstream(string nodeId, bool includeRelationships) {
      var seen = new HashSet<string>();
      var result = new List<string>();
      WalkDown(nodeId, seen, result, includeRelationships);
      return result;
    }

    void WalkDown(string id, HashSet<string> seen, List<string> result, bool includeRelationships) {
      foreach (var edge in Edges.Where(e => e.From == id)) {
        if (!includeRelationships && edge.Kind == "relationship") continue;
        if (seen.Contains(edge.To)) continue;
        seen.Add(edge.To);
        result.Add(edge.To);
        WalkDown(edge.To, seen, result, includeRelationships);
      }
    }

    public Dictionary<string, int> Levels() {
      return Levels(true);
    }

    public Dictionary<string, int> Levels(bool includeRelationships) {
      var levels = new Dictionary<string, int>();
      var visiting = new HashSet<string>();
      foreach (var node in Nodes) Visit(node.Id, levels, visiting, includeRelationships);
      return levels;
    }

    int Visit(string id, Dictionary<string, int> levels, HashSet<string> visiting, bool includeRelationships) {
      if (levels.ContainsKey(id)) return levels[id];
      if (visiting.Contains(id)) return 0;
      visiting.Add(id);
      var parents = Edges.Where(e => e.To == id && (includeRelationships || e.Kind != "relationship")).Select(e => e.From).ToList();
      var node = NodeById(id);
      var fallback = node != null && node.Type == "source" ? 0 : node != null && node.Type == "source-schema" ? 1 : node != null && node.Type == "source-table" ? 2 : node != null && node.Type == "table" ? 3 : node != null && node.Type == "column" ? 4 : 5;
      var level = parents.Count == 0 ? fallback : parents.Select(p => Visit(p, levels, visiting, includeRelationships)).Max() + 1;
      visiting.Remove(id);
      levels[id] = level;
      return level;
    }
  }

  public class Node {
    public readonly string Id;
    public readonly string Display;
    public readonly string Type;
    public readonly string Table;
    public readonly string Expression;
    public readonly string Path;
    public Node(string id, string display, string type, string table, string expression, string path) {
      Id = id;
      Display = display;
      Type = type;
      Table = table;
      Expression = expression ?? "";
      Path = path ?? "";
    }
    public override string ToString() { return Type + "  " + Display; }
  }

  public class Edge {
    public string Id;
    public string From;
    public string To;
    public string Kind;
    public string FromCardinality;
    public string ToCardinality;
    public string Direction;
    public bool IsActive;
  }

  public class TmdlFile {
    public string Path;
    public string Content;
  }

  public class MeasureDef {
    public string Table;
    public string Name;
    public string Expression;
  }

  public class RelationshipDef {
    public string Name;
    public string From;
    public string To;
    public string FromCardinality;
    public string ToCardinality;
    public string CrossFilteringBehavior;
    public bool IsActive;
  }

  public class SourceStep {
    public string Name;
    public string Kind;
  }

  public class PartitionDef {
    public string Name;
    public string Expression;
  }

  public class RefSet {
    public readonly HashSet<string> Columns = new HashSet<string>();
    public readonly HashSet<string> Measures = new HashSet<string>();
  }

  public static class Branding {
    public static Bitmap CreateLogoBitmap(int size, Color backColor) {
      var bitmap = new Bitmap(size, size);
      using (var g = Graphics.FromImage(bitmap))
      using (var linePen = new Pen(Color.FromArgb(199, 210, 254), Math.Max(2F, size / 18F)))
      using (var ringPen = new Pen(Color.White, Math.Max(2F, size / 20F)))
      using (var fillBrush = new SolidBrush(Color.White))
      using (var tileBrush = new SolidBrush(Theme.Primary)) {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(backColor);
        FillRoundedRect(g, tileBrush, new RectangleF(1, 1, size - 2, size - 2), Math.Max(5F, size * 0.22F));
        linePen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
        linePen.EndCap = System.Drawing.Drawing2D.LineCap.Round;

        var left = new PointF(size * 0.26F, size * 0.52F);
        var top = new PointF(size * 0.72F, size * 0.26F);
        var mid = new PointF(size * 0.72F, size * 0.52F);
        var bottom = new PointF(size * 0.72F, size * 0.78F);
        var join = new PointF(size * 0.42F, size * 0.52F);

        g.DrawLine(linePen, left, join);
        g.DrawBezier(linePen, join, new PointF(size * 0.50F, size * 0.48F), new PointF(size * 0.56F, size * 0.26F), top);
        g.DrawLine(linePen, join, mid);
        g.DrawBezier(linePen, join, new PointF(size * 0.50F, size * 0.58F), new PointF(size * 0.56F, size * 0.78F), bottom);

        DrawNode(g, ringPen, fillBrush, left, size * 0.11F);
        DrawNode(g, ringPen, fillBrush, top, size * 0.11F);
        DrawNode(g, ringPen, fillBrush, mid, size * 0.11F);
        DrawNode(g, ringPen, fillBrush, bottom, size * 0.11F);
      }
      return bitmap;
    }

    public static Icon CreateAppIcon() {
      var bitmap = CreateLogoBitmap(64, Color.FromArgb(238, 242, 255));
      return Icon.FromHandle(bitmap.GetHicon());
    }

    static void DrawNode(Graphics g, Pen ringPen, Brush fillBrush, PointF center, float radius) {
      g.DrawEllipse(ringPen, center.X - radius, center.Y - radius, radius * 2, radius * 2);
      var inner = new RectangleF(center.X - radius * 0.42F, center.Y - radius * 0.28F, radius * 0.84F, radius * 0.56F);
      FillRoundedRect(g, fillBrush, inner, Math.Max(4F, radius * 0.18F));
    }

    static void FillRoundedRect(Graphics g, Brush brush, RectangleF rect, float radius) {
      using (var path = new System.Drawing.Drawing2D.GraphicsPath()) {
        path.AddArc(rect.Left, rect.Top, radius * 2, radius * 2, 180, 90);
        path.AddArc(rect.Right - radius * 2, rect.Top, radius * 2, radius * 2, 270, 90);
        path.AddArc(rect.Right - radius * 2, rect.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
      }
    }
  }
}
