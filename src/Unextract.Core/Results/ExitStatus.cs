namespace Unextract.Core.Results;

// 終了状態の3区分 (SPEC §2)。細分化した終了コード体系は設けない。
public enum ExitStatus
{
    Success,
    UserCancelled,
    Error,
}

public static class ExitCodes
{
    public const int Success = 0;
    public const int Error = 1;
    public const int UserCancelled = 2;

    public static int ToProcessExitCode(ExitStatus status) => status switch
    {
        ExitStatus.Success => Success,
        ExitStatus.UserCancelled => UserCancelled,
        ExitStatus.Error => Error,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}
