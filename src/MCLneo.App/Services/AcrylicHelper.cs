using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MCLneo.App.Services;

/// <summary>
/// Applies Windows' acrylic backdrop to transparent WPF popups. This is what
/// gives the glass panels a blurred, refracted backdrop instead of only alpha.
/// Falls back silently when the compositor or OS build does not support it.
/// </summary>
public static class AcrylicHelper
{
    private const int WcaAccentPolicy = 19;

    public static void Enable(FrameworkElement visual, bool dark)
    {
        try
        {
            var source = PresentationSource.FromVisual(visual) as HwndSource;
            if (source is null || source.Handle == IntPtr.Zero)
            {
                return;
            }

            var accent = new AccentPolicy
            {
                AccentState = 4, // ACCENT_ENABLE_ACRYLICBLURBEHIND
                AccentFlags = 2,
                GradientColor = dark ? unchecked((int)0x55101014) : unchecked((int)0x33FFFFFF),
                AnimationId = 0
            };
            var size = Marshal.SizeOf<AccentPolicy>();
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, pointer, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WcaAccentPolicy,
                    Data = pointer,
                    SizeOfData = size
                };
                SetWindowCompositionAttribute(source.Handle, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
        catch
        {
            // Acrylic is an enhancement; the translucent XAML remains the fallback.
        }
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }
}
