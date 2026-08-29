using Spectre.Console.Cli;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace BBDown.Commands;

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
public class LoginSettings : CommandSettings
{
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
public class LoginCommand : AsyncCommand<LoginSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, LoginSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            return await BBDownLoginUtil.LoginWEB(cancellationToken) ? 0 : 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
    }
}
