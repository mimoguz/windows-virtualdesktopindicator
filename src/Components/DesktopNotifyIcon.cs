﻿using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Text;
using Microsoft.Win32;
using VirtualDesktopIndicator.Config;
using VirtualDesktopIndicator.Native;
using VirtualDesktopIndicator.Native.Constants;
using VirtualDesktopIndicator.Native.Hooks;
using VirtualDesktopIndicator.Native.VirtualDesktop;
using VirtualDesktopIndicator.Utils;
using Timer = System.Windows.Forms.Timer;

namespace VirtualDesktopIndicator.Components;

internal class DesktopNotifyIcon : IDisposable
{
    #region Theme

    private const string RegistryThemeDataPath =
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private enum Theme
    {
        Light,
        Dark
    }

    private static readonly Dictionary<Theme, Color> DefaultIconColors = new()
    {
        { Theme.Dark, Color.White },
        { Theme.Light, Color.Black },
    };

    private static readonly Dictionary<Theme, Color> ThemesColors = new()
    {
        { Theme.Dark, Color.White },
        { Theme.Light, Color.Black },
    };

    private static readonly Dictionary<Theme, Color> ThemesColorContrasts = new()
    {
        { Theme.Dark, Color.Black },
        { Theme.Light, Color.White }
    };

    private Color CurrentIconColor
    {
        get
        {
            if (_systemTheme == Theme.Light)
            {
                return UserConfig.Current.IconColorForLightThemeDesktop.ContainsKey(CurrentVirtualDesktop)
                    ? UserConfig.Current.IconColorForLightThemeDesktop[CurrentVirtualDesktop]
                    : DefaultIconColors[_systemTheme];
            }
            else
            {
                return UserConfig.Current.IconColorForDarkThemeDesktop.ContainsKey(CurrentVirtualDesktop)
                    ? UserConfig.Current.IconColorForDarkThemeDesktop[CurrentVirtualDesktop]
                    : DefaultIconColors[_systemTheme];
            }
        }
    }
    private Color CurrentThemeColor => ThemesColors[_systemTheme];
    private Color CurrentThemeColorContrast => ThemesColorContrasts[_systemTheme];

    private Theme _cachedSystemTheme;
    private Theme _systemTheme;

    private RegistryMonitor? _registryMonitor;

    #endregion

    #region Drawing Constants

    private static string FontName => UserConfig.Current.FontName;

    private static int BorderThickness => Width / BaseWidth;
    private static int FontSize => (int) Math.Ceiling(Width / 1.5);

    private static FontStyle FontStyle => UserConfig.Current.FontStyle;

    // Default windows tray icon size
    private const int BaseHeight = 16;
    private const int BaseWidth = 16;

    // We use half the size, because otherwise the image is rendered with incorrect anti-aliasing
    private static int Height
    {
        get
        {
            var height = User32.GetSystemMetrics(SystemMetric.SM_CYICON) / 2;
            return height < BaseHeight ? BaseHeight : height;
        }
    }

    private static int Width
    {
        get
        {
            var width = User32.GetSystemMetrics(SystemMetric.SM_CXICON) / 2;
            return width < BaseWidth ? BaseWidth : width;
        }
    }

    #endregion

    private readonly DesktopNameForm _desktopDisplay = new(FontName);
    private readonly IVirtualDesktopManager _virtualDesktopManager;

    private readonly MouseScrollHook _mouseHook;

    private readonly NotifyIcon _notifyIcon;
    private readonly Timer _timer;

    private uint CurrentVirtualDesktop => _virtualDesktopManager.Current() + 1;

    private uint _lastVirtualDesktop;
    private string? _cachedDisplayText;

    private readonly StringBuilder _foregroundWindowTextBuffer = new();
    private readonly Timer _taskViewDetectionTimer;
    private bool _taskViewOpen;
    private bool _taskViewClick;

    public DesktopNotifyIcon(IVirtualDesktopManager virtualDesktop)
    {
        _virtualDesktopManager = virtualDesktop;

        _notifyIcon = new() {ContextMenuStrip = CreateContextMenu()};
        _notifyIcon.MouseClick += OnNotifyIconClick;

        _timer = new() {Enabled = false};
        _timer.Tick += OnTimerTick;

        _taskViewDetectionTimer = new() {Enabled = false, Interval = 500};
        _taskViewDetectionTimer.Tick += OnTaskViewDetectionTimerTick;

        _mouseHook = new();
        _cachedSystemTheme = _systemTheme = GetSystemTheme();

        InitMouseHook();
        InitRegistryMonitor();
    }

    #region Lifecycles

    #region NotifyIcon

    public void Show()
    {
        _registryMonitor?.Start();

        _notifyIcon.Visible = true;
        _timer.Enabled = true;
        _taskViewDetectionTimer.Enabled = true;
    }

    public void Dispose()
    {
        StopRegistryMonitor();
        StopMouseHook();

        _notifyIcon.Dispose();
        _timer.Dispose();
        _taskViewDetectionTimer.Dispose();
    }

    #endregion

    #region MouseHook

    private void InitMouseHook()
    {
        _mouseHook.Register();
        _mouseHook.MouseScroll += OnMouseScroll;
    }

    private void StopMouseHook()
    {
        _mouseHook.MouseScroll -= OnMouseScroll;
        _mouseHook.Unregister();
    }

    #endregion

    #region RegistryMonitor

    private void InitRegistryMonitor()
    {
        _registryMonitor = new(RegistryThemeDataPath);

        _registryMonitor.RegChanged += OnRegistryChanged;
        _registryMonitor.Error += OnRegistryError;
    }

    private void StopRegistryMonitor()
    {
        if (_registryMonitor == null) return;

        _registryMonitor.Stop();

        _registryMonitor.RegChanged -= OnRegistryChanged;
        _registryMonitor.Error -= OnRegistryError;

        _registryMonitor = null;
    }

    #endregion

    #endregion

    #region Events
    
    private void OnMouseScroll(object? sender, MouseScrollEventArgs e)
    {
        // We need to call the virtual desktop functions from a STA thread, because of COM quirks
        var thread = new Thread(() =>
        {
            var rect = Shell32.GetNotifyIconRect(_notifyIcon);
            var cursor = Cursor.Position;
            
            if (rect.left <= cursor.X && rect.right >= cursor.X && rect.top <= cursor.Y && rect.bottom >= cursor.Y)
            {
                if (e.Delta < 0)
                {
                    _virtualDesktopManager.SwitchForward();
                }
                else
                {
                    _virtualDesktopManager.SwitchBackward();
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
    
    private void OnTaskViewDetectionTimerTick(object? sender, EventArgs e)
    {
        User32.GetWindowText(User32.GetForegroundWindow(), _foregroundWindowTextBuffer, 256);
        
        var taskViewOpened = _foregroundWindowTextBuffer.ToString().Equals(Constants.TaskView);

        if (taskViewOpened)
        {
            _taskViewClick = false;
        }

        _taskViewOpen = taskViewOpened || _taskViewClick;
        
        _foregroundWindowTextBuffer.Clear();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        try
        {
            if (CurrentVirtualDesktop == _lastVirtualDesktop) return;
            if (UserConfig.Current.NotificationsEnabled)
            {
                _desktopDisplay.Show(_virtualDesktopManager.CurrentDisplayName(), CurrentThemeColor, CurrentThemeColorContrast);
            }

            _cachedDisplayText = CurrentVirtualDesktop < 100 ? CurrentVirtualDesktop.ToString() : "++";
            _lastVirtualDesktop = CurrentVirtualDesktop;

            RedrawIcon();
        }
        catch (Exception ex)
        {
            // Do not spam with error messages
            _timer.Enabled = false;
            
            MessageBox.Show(
                $"{Constants.AppName} encountered an unhandled error:\n{ex}",
                Constants.AppName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );

            Application.Exit();
        }
    }

    private void OnRegistryChanged(object? sender, EventArgs e)
    {
        _systemTheme = GetSystemTheme();
        if (_systemTheme == _cachedSystemTheme) return;

        RedrawIcon();
        _cachedSystemTheme = _systemTheme;
    }

    private void OnRegistryError(object sender, ErrorEventArgs e)
    {
        StopRegistryMonitor();
    }

    private void OnNotifyIconClick(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button == MouseButtons.Left)
        {
            _taskViewClick = false;

            if (!_taskViewOpen)
            {
                Shell32.ShellExecuteClsid(ClsIds.TaskView);
                _taskViewClick = true;
            }
        }
    }

    #endregion

    #region Icon

    private void RedrawIcon()
    {
        _notifyIcon.Icon = GenerateIcon();
    }

    private Icon? GenerateIcon()
    {
        var font = new Font(FontName, FontSize, FontStyle, GraphicsUnit.Pixel);
        var brush = new SolidBrush(CurrentIconColor);
        var bitmap = new Bitmap(Width, Height);

        var graphics = Graphics.FromImage(bitmap);

        graphics.SmoothingMode = SmoothingMode.HighSpeed;
        graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;

        graphics.Clear(Color.Transparent);

        // Draw border
        // The g.DrawRectangle always uses anti-aliasing and border looks very poor at such small resolutions
        // Just draw four lines around the edges
        var pen = new Pen(CurrentIconColor, 1);
        for (var thickness = 0; thickness < BorderThickness; thickness++)
        {
            // Top
            graphics.DrawLine(pen, 0, thickness, Width - 1, thickness);
            // Right
            graphics.DrawLine(pen, thickness, 0, thickness, Height - 1);
            // Left
            graphics.DrawLine(pen, Width - 1 - thickness, 0, Width - 1 - thickness, Height - 1);
            // Bottom
            graphics.DrawLine(pen, 0, Height - 1 - thickness, Width - 1, Height - 1 - thickness);
        }

        // Draw text
        var textSize = graphics.MeasureString(_cachedDisplayText, font);

        // Calculate padding to center the text
        // We can't assume that g.DrawString will round the coordinates correctly, so we do it manually
        var offsetX = (float) Math.Ceiling((Width - textSize.Width + UserConfig.Current.AdditionalXOffset) / 2);
        var offsetY = (float) Math.Ceiling((Height - textSize.Height + UserConfig.Current.AdditionalYOffset) / 2);

        graphics.DrawString(_cachedDisplayText, font, brush, offsetX, offsetY);

        // Create icon from bitmap and return it
        // bitmapText.GetHicon() can throw exception
        try
        {
            return Icon.FromHandle(bitmap.GetHicon());
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region Dynamic Controls

    private ContextMenuStrip CreateContextMenu()
    {
        var menu = new ContextMenuStrip();

        var autorunItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = Autorun.IsActive()
        };
        autorunItem.Click += (_, _) =>
        {
            autorunItem.Checked = !autorunItem.Checked;

            if (autorunItem.Checked)
            {
                Autorun.Enable();
            }
            else
            {
                Autorun.Disable();
            }
        };
        
        var notificationsItem = new ToolStripMenuItem("Enable Notifications")
        {
            Checked = UserConfig.Current.NotificationsEnabled
        };
        notificationsItem.Click += (_, _) =>
        {
            notificationsItem.Checked = !notificationsItem.Checked;
            
            UserConfig.Current.NotificationsEnabled = notificationsItem.Checked;
            UserConfig.Current.Save();
        };

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => Application.Exit();

        menu.Items.Add(autorunItem);
        menu.Items.Add(notificationsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        return menu;
    }

    #endregion

    #region Static

    private static Theme GetSystemTheme()
    {
        return (int) (Registry.GetValue(RegistryThemeDataPath, "SystemUsesLightTheme", 0) ?? 0) == 1
            ? Theme.Light
            : Theme.Dark;
    }

    #endregion
}