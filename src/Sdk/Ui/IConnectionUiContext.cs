namespace Lionear.SqlExplorer.Sdk.Ui;

/// <summary>
/// Read/write access to the connection dialog's field values, handed to a provider's custom advanced
/// view (<see cref="ICustomConnectionUi"/>). The view uses it to stay in sync with the values the host
/// collects and passes to <c>IDbProvider.BuildConnectionString</c>; keys are <c>ConnectionField.Key</c>s.
/// </summary>
public interface IConnectionUiContext
{
    /// <summary>Current value for a field key, or null if the provider declared no such field.</summary>
    string? GetValue(string key);

    /// <summary>Write a field value; ignored if the key is not one of the provider's declared fields.</summary>
    void SetValue(string key, string? value);
}
