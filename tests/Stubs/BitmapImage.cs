/*
 * Stand-in for Microsoft.UI.Xaml.Media.Imaging.BitmapImage.
 *
 * DocumentItem holds its thumbnail as a XAML bitmap.  Under UNIT_TEST
 * that using directive is compiled out, and this empty type takes the
 * name so the model builds without WinUI.  Nothing here draws.
 */

namespace Gladhen3.Models;

public sealed class BitmapImage
{
}
