using System.Globalization;
using System.Windows;
using Unextract.Gui.Models;
using Unextract.Gui.ViewModels;

namespace Unextract.Gui.Views;

// The single confirmation for a whole delete batch. Cancel is the default and Escape action. With no deletable
// Target it only explains the exclusions and offers no delete button.
public partial class DeletionConfirmWindow : Window
{
    internal DeletionConfirmWindow(DeletionPlan plan, string listText)
    {
        InitializeComponent();
        bool any = plan.Items.Count != 0;
        bool fast = plan.Mode == CliMode.Fast;
        if (!any)
        {
            Title = "削除";
            HeadingText.Text = "削除できるTargetがありません。";
            IrreversibleText.Text = "選択中のTargetはすべて削除対象から除外されます。理由は下の一覧のとおりです。削除は開始しません。";
            DeleteButton.Visibility = Visibility.Collapsed;
            CancelButton.Content = "閉じる";
        }
        else
        {
            HeadingText.Text = $"選択中の {plan.Items.Count:N0} 件のTargetから、削除候補のファイルを削除します。";
            DeleteButton.Content = $"{plan.Items.Count:N0} 件のTargetを削除する";
        }
        FastPanel.Visibility = fast && any ? Visibility.Visible : Visibility.Collapsed;
        FastText.Text = MainViewModel.FastWarning;
        ModeText.Text = fast ? "Fast（パスとサイズのみ。内容は比較しません）" : "Strict（アーカイブの内容と全バイト一致したファイルだけ）";
        TargetCountText.Text = $"{plan.Items.Count:N0} 件（一覧の順に1件ずつ実行します）";
        FileCountText.Text = string.Create(CultureInfo.CurrentCulture,
            $"{plan.TotalCandidates:N0} ファイル（解析時点の最大件数）。実行時にCLIが再検証するため、解析後に変化したファイルなど、削除されないファイルがあり得ます。");
        SizeText.Text = SizeFormat.Bytes(plan.TotalLength) + "（論理サイズの合計。物理的に解放される容量ではありません）";
        ExcludedCountText.Text = plan.Excluded.Count == 0 ? "なし" : $"{plan.Excluded.Count:N0} 件（理由は下の一覧のとおり。削除しません）";
        HiddenPanel.Visibility = plan.HiddenCount != 0 ? Visibility.Visible : Visibility.Collapsed;
        HiddenText.Text = $"表示フィルタで非表示の選択済みTarget {plan.HiddenCount:N0} 件を含みます（対象から外していません）。一覧で［表示フィルタで非表示］の印が付いています。";
        Body.Text = listText;
        Loaded += (_, _) =>
        {
            MainWindow.FitToWorkArea(this);
            CancelButton.Focus();
        };
    }

    private void DeleteClicked(object sender, RoutedEventArgs e) => DialogResult = true;
}
