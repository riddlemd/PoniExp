namespace PoniExp.Core.Crypto;

/// <summary>Writes files that hold secrets: owner read/write only on Unix, written atomically.</summary>
public static class PrivateFiles
{
    public static void WriteAllText(string path, string contents) =>
        WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(contents));

    public static void WriteAllBytes(string path, byte[] contents)
    {
        var temp = path + ".tmp";
        using (var stream = OperatingSystem.IsWindows()
                   ? new FileStream(temp, FileMode.Create, FileAccess.Write)
                   : new FileStream(temp, new FileStreamOptions
                   {
                       Mode = FileMode.Create,
                       Access = FileAccess.Write,
                       UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                   }))
        {
            stream.Write(contents);
        }

        File.Move(temp, path, overwrite: true);
    }
}
