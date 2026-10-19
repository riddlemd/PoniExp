using PoniExp.Router;
using PoniExp.Router.Admin;

if (args is ["admin", .. var rest])
{
    return await AdminCli.RunAsync(rest, Console.Out);
}

var app = RouterHost.Build(args);
await app.RunAsync();
return 0;
