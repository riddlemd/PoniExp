using PoniExp.Server;
using PoniExp.Server.Admin;

const string Usage = """
    poniexp router [--PoniExp:Port=5443 ...]    run the relay (settings also from PoniExp__* env vars)
    poniexp admin <command>                      manage a running router: list, approve, reject, remove, certificate
    """;

switch (args)
{
    case ["router", .. var rest]:
        await RouterHost.Build(rest).RunAsync();
        return 0;
    case ["admin", .. var rest]:
        return await AdminCli.RunAsync(rest, Console.Out);
    default:
        Console.Error.WriteLine(Usage.TrimEnd());
        return 2;
}
