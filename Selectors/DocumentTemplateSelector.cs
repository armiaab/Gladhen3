using Gladhen3.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gladhen3.Selectors;

public partial class DocumentTemplateSelector : DataTemplateSelector
{
    public DataTemplate? PageTemplate { get; set; }

    public DataTemplate? SectionBreakTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);

    protected override DataTemplate? SelectTemplateCore(object item)
        => item is DocumentItem { Type: DocumentType.SectionBreak } ? SectionBreakTemplate : PageTemplate;
}
