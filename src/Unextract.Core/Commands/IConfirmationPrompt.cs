namespace Unextract.Core.Commands;

// delete の確認 (docs/spec/cli.md#arguments、docs/spec/cli.md#confirmation)。Ask の戻り値 null は EOF。
public interface IConfirmationPrompt
{
    bool IsInteractive { get; }

    string? Ask(string prompt);
}
