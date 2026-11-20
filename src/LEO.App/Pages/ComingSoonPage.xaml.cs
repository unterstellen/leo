using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Leo.App.Pages;

public sealed record ComingSoonInfo(string Title, string Milestone, string Description);

/// <summary>Placeholder for sections that land in later milestones (LEO.md §12).</summary>
public sealed partial class ComingSoonPage : Page
{
    public ComingSoonPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is ComingSoonInfo info)
        {
            TitleText.Text = info.Title;
            MilestoneText.Text = $"Coming in milestone {info.Milestone}";
            DescriptionText.Text = info.Description;
        }
    }
}
