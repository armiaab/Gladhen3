using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace Gladhen3.Converters;

/// <summary>
/// Returns Visible when value is null, Collapsed otherwise.
/// Used to show fallback icons when thumbnail is not available.
/// </summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, string language)
    {
        return value == null ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Returns Visible when value is not null, Collapsed otherwise.
/// </summary>
public class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, string language)
    {
  return value != null ? Visibility.Visible : Visibility.Collapsed;
  }

    public object ConvertBack(object? value, Type targetType, object? parameter, string language)
    {
 throw new NotImplementedException();
    }
}
