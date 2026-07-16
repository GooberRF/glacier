namespace Ged.App;

/// <summary>
/// The Windows <see cref="IViewportHost"/>: hosts each render pane in a Win32
/// child window (see <see cref="Win32"/>) whose WndProc forwards mouse/keyboard
/// input to the owning viewport. This is the only place the child-HWND embedding
/// model is used; L3's OpenGL host replaces it wholesale behind <see cref="IViewportHost"/>.
/// </summary>
internal sealed class Win32ViewportHost : IViewportHost
{
    public nint CreateChild(nint parent, int width, int height, IViewportInput input) =>
        Win32.CreateChild(parent, width, height, input);

    public void DestroyChild(nint handle) => Win32.DestroyChild(handle);

    public (int Width, int Height) GetClientSize(nint handle) => Win32.GetClientSize(handle);

    public bool IsKeyPhysicallyDown(int virtualKey) => Win32.IsKeyPhysicallyDown(virtualKey);
}
