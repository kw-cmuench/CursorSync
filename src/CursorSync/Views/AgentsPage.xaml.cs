using System.Windows.Controls;
using System.Windows.Input;
using CursorSync.ViewModels;

namespace CursorSync.Views;

public partial class AgentsPage : UserControl
{
    public AgentsPage()
    {
        InitializeComponent();
    }

    private void AgentRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border)
            return;
        if (border.DataContext is not AgentItemViewModel agent)
            return;
        if (DataContext is MainViewModel vm)
            vm.Agents.FocusAgentCommand.Execute(agent);
    }
}
