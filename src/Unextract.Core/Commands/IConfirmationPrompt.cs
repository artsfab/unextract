namespace Unextract.Core.Commands;

// delete の確認 (SPEC §2、§3.2)。Ask の戻り値 null は EOF。
public interface IConfirmationPrompt
{
    bool IsInteractive { get; }

    string? Ask(string prompt);
}
