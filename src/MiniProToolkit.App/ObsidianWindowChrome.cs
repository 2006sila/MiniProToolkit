using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace MiniProToolkit.App;

/// <summary>
/// 方案 A「曜石控制舱」的原生窗口外框着色：所有功能窗共用同一份 DWM 调用与同一组常量。
/// 只在 Windows 10 1809+/Windows 11 生效，纯装饰用途：
/// <list type="bullet">
/// <item>DWM 不可用（dwmapi.dll 缺失、旧系统、被策略禁用）时全部异常吞掉，窗口照常启动并保留系统默认标题栏。</item>
/// <item>不自绘标题栏，因此不会破坏拖动 / resize / 系统菜单。</item>
/// <item>只读资源字典里的色值，不读写任何配置，也不影响 OSD / 任务栏 overlay 由用户配置写入的颜色。</item>
/// </list>
/// 常量取值见 dwmapi.h（DWMWA_USE_IMMERSIVE_DARK_MODE 在新 SDK 里是 20）。
/// </summary>
internal static class ObsidianWindowChrome
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmBorderColor = 34;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;
    // DWMWA_WINDOW_CORNER_PREFERENCE / DWMWCP_DONOTROUND (Windows 11 22000+).
    private const int DwmWindowCornerPreference = 33;
    private const int DwmWindowCornerDoNotRound = 1;

    // 与 Themes/Obsidian.xaml 同值的 fallback：设计器宿主、未合并字典的窗口或键缺失时仍保持深色。
    private static readonly Color WindowBackgroundFallback = Color.FromRgb(0x0B, 0x10, 0x18);
    private static readonly Color CaptionFallback = Color.FromRgb(0x0C, 0x13, 0x20);
    private static readonly Color TextFallback = Color.FromRgb(0xE5, 0xED, 0xF7);
    private static readonly Color BorderFallback = Color.FromRgb(0x22, 0x30, 0x44);

    // 每个窗口只挂一次 SourceInitialized，重复调用 Apply 不会被调用两次 DWM。
    private static readonly ConditionalWeakTable<Window, object> Hooked = new();

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// 在构造/初始化阶段调用：窗口获得 HWND 后一次性套用深色原生标题栏。
    /// 必须在 InitializeComponent 之后调用，这样 <paramref name="window"/> 自己的资源字典已经合并。
    /// </summary>
    internal static void Apply(Window window) => Apply(window, window);

    /// <summary>同上，显式指定颜色查找用的资源作用域（通常就是窗口本身）。</summary>
    internal static void Apply(Window window, FrameworkElement resourceScope)
    {
        if (window is null || resourceScope is null)
            return;

        try
        {
            if (!Hooked.TryAdd(window, window))
                return;
            window.SourceInitialized += (_, _) => ApplyTitleBar(window, resourceScope);
        }
        catch (Exception)
        {
            // 挂钩失败同样只是外观问题：保持系统默认标题栏。
        }
    }

    /// <summary>
    /// 立即套用（调用方已确保 HWND 存在，例如主窗口 <c>ApplyObsidianPresentation</c> 里）。
    /// 任何 DWM 异常都在这里被吞掉，绝不影响窗口启动。
    /// </summary>
    internal static void ApplyTitleBar(Window window, FrameworkElement resourceScope)
    {
        if (window is null || resourceScope is null)
            return;

        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
                return;

            var enabled = 1;
            // 失败（旧系统/被策略禁用）时保持系统默认标题栏，不做自绘，避免破坏拖动/resize/系统菜单。
            if (DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
                return;

            // 窗口底色只作为主题一致性检查项读一次；沉浸式深色模式由系统按上面的开关推导底色。
            _ = ToColorReference(ColorOf(resourceScope, "ObsidianWindowBackgroundBrush", WindowBackgroundFallback));
            var caption = ToColorReference(ColorOf(resourceScope, "ObsidianTopBarBackgroundBrush", CaptionFallback));
            var text = ToColorReference(ColorOf(resourceScope, "ObsidianTextPrimaryBrush", TextFallback));
            var border = ToColorReference(ColorOf(resourceScope, "ObsidianCardBorderBrush", BorderFallback));
            _ = DwmSetWindowAttribute(handle, DwmCaptionColor, ref caption, sizeof(int));
            _ = DwmSetWindowAttribute(handle, DwmTextColor, ref text, sizeof(int));
            _ = DwmSetWindowAttribute(handle, DwmBorderColor, ref border, sizeof(int));
        }
        catch (Exception)
        {
            // DllNotFound / EntryPointNotFound / 任何 DWM 互操作异常：标题栏着色是可选装饰，直接放弃。
        }
    }

    /// <summary>
    /// 仅供任务栏透明浮层使用：关掉 Windows 11 给顶层窗口自动加的圆角，让状态文字直接显示在
    /// 任务栏像素上（无黑底、无圆角、无边框）。Windows 10/旧系统的 dwmapi 不认这个属性，调用
    /// 失败或 dwmapi 不可用时静默跳过；普通不透明功能窗和 OSD 的圆角/标题栏着色完全不受影响。
    /// </summary>
    internal static void ApplyDontRoundCorners(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
            return;
        try
        {
            var preference = DwmWindowCornerDoNotRound;
            _ = DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref preference, sizeof(int));
        }
        catch (Exception)
        {
            // DwmNotFound / EntryPointNotFound / 任何 DWM 互操作异常：圆角设置属于可选装饰，直接放弃。
        }
    }

    /// <summary>COLORREF：0x00BBGGRR。</summary>
    private static int ToColorReference(Color color) => color.R | (color.G << 8) | (color.B << 16);

    private static Color ColorOf(FrameworkElement resourceScope, string key, Color fallback) =>
        resourceScope.TryFindResource(key) is SolidColorBrush brush ? brush.Color : fallback;
}
