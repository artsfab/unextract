using System.Text;
using Unextract.Core.Results;

namespace Unextract.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (CliApplication.IsMachineMode(args))
        {
            try
            {
                return ExitCodes.ToProcessExitCode(CliApplication.RunMachine(args, Console.OpenStandardOutput(), Console.Error));
            }
            catch (Exception ex)
            {
                CliApplication.ReportUnwritable(Console.Error, ex);
                return ExitCodes.ToProcessExitCode(ExitStatus.Error);
            }
        }

        // 日本語や CP437 由来の文字を含む名前を文字化けさせないため、実行中だけ出力を UTF-8 にし、終了時に元へ戻す
        // (コンソールのコードページは親のコンソールと共有されるため)。
        var original = TrySetOutputEncoding(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        try
        {
            return ExitCodes.ToProcessExitCode(CliApplication.Run(args, Console.Out, Console.Error));
        }
        finally
        {
            if (original is not null)
            {
                TrySetOutputEncoding(original);
            }
        }
    }

    // 設定できない環境 (コンソールが無いなど) では何もしない。戻り値は変更前のエンコーディング。
    private static Encoding? TrySetOutputEncoding(Encoding encoding)
    {
        try
        {
            var original = Console.OutputEncoding;
            Console.OutputEncoding = encoding;
            return original;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
