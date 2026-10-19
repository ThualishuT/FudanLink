using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

[assembly:AssemblyTitle("FudanLink Setup")]
[assembly:AssemblyProduct("FudanLink")]
[assembly:AssemblyVersion("1.3.1.0")]

internal static class Setup
{
    [STAThread]
    private static int Main()
    {
        const string directory=@"C:\Program Files\FudanLink";
        try
        {
            if(Directory.Exists(directory) && (File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)
                throw new IOException("The installation folder must not be a link.");
            Directory.CreateDirectory(directory);
            foreach(string name in new[]{"FudanLink.exe","FudanLink.exe.config"})
            {
                string target=Path.Combine(directory,name),temp=target+".installing";
                try
                {
                    using(var resource=Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                    using(var output=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None))
                    {
                        if(resource==null)throw new IOException("Installation resource missing.");
                        resource.CopyTo(output);
                    }
                    if(File.Exists(target))File.Replace(temp,target,null);else File.Move(temp,target);
                }
                finally{if(File.Exists(temp))File.Delete(temp);}
            }
            return 0;
        }
        catch
        {
            MessageBox.Show("安装未完成。请关闭 FudanLink 后重试安装。","FudanLink",MessageBoxButtons.OK,MessageBoxIcon.Information);
            return 1;
        }
    }
}
