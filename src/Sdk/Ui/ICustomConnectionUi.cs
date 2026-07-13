using Avalonia.Controls;

namespace Lionear.SqlExplorer.Sdk.Ui;

/// <summary>
/// Optional capability an <c>IDbProvider</c> may also implement to supply its own Avalonia view for the
/// connection dialog's Advanced section, instead of the host-generated form built from the declared
/// <c>Advanced</c> <c>ConnectionField</c>s (Route B, Notes §4.4). Useful when advanced options are
/// interdependent (e.g. an auth mode that shows/hides other fields).
/// </summary>
/// <remarks>
/// Data still flows through the provider's declared <c>ConnectionField</c>s: the view reads and writes
/// their values via <see cref="IConnectionUiContext"/>, so <c>BuildConnectionString</c>, save and the
/// import flow are unaffected. This assembly and Avalonia are shared across the plugin ALC boundary
/// (<c>ProviderLoadContext</c>) so the returned control has a single type identity with the host.
/// Providers that don't implement this get the host-generated form.
/// </remarks>
public interface ICustomConnectionUi
{
    /// <summary>
    /// Build the Advanced-section view. Read and write the connection field values through
    /// <paramref name="context"/> (keyed by <c>ConnectionField.Key</c>).
    /// </summary>
    Control CreateAdvancedView(IConnectionUiContext context);
}
