using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Leo.App;

/// <summary>
/// "Has a value": non-null and not an empty string. Returns <see cref="Visibility"/> when bound to a Visibility
/// property (x:Bind only auto-converts bool→Visibility when there is NO converter; returning a bool into
/// Visibility crashes XAML with E_NOINTERFACE), otherwise a bool.
/// </summary>
public sealed partial class NotNullToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, string language)
    {
        var hasValue = value is string s ? s.Length > 0 : value is not null;
        return targetType == typeof(Visibility) ? (hasValue ? Visibility.Visible : Visibility.Collapsed) : hasValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
