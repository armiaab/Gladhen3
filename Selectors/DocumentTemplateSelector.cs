using Gladhen3.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gladhen3.Selectors;

/// <summary>
/// Picks the page card or the section band for a row of the document list.
/// </summary>
/// <remarks>
/// Both live in one collection so that the built-in drag reorder keeps working - see
/// <see cref="DocumentType.SectionBreak"/> - which means the view has to choose a template
/// per row rather than per list.
/// </remarks>
public partial class DocumentTemplateSelector : DataTemplateSelector
{
    public DataTemplate? PageTemplate { get; set; }

    public DataTemplate? SectionBreakTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);

    protected override DataTemplate? SelectTemplateCore(object item)
        => item is DocumentItem { Type: DocumentType.SectionBreak } ? SectionBreakTemplate : PageTemplate;
}
