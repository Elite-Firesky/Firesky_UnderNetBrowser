using MelonLoader;
using System.Reflection;

[assembly: AssemblyTitle("Firesky_UnderNetBrowser")]
[assembly: AssemblyDescription("In-game TV web browser for Schedule I (WebView2/Edge) - browse YouTube, Watch2Gether and web content on in-game TVs")]
[assembly: AssemblyConfiguration("Release")]
[assembly: AssemblyCompany("Firesky")]
[assembly: AssemblyProduct("Firesky_UnderNetBrowser")]
[assembly: AssemblyCopyright("Copyright Firesky  2026")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: MelonInfo(typeof(Firesky.UnderNetBrowser.UnderNetBrowserMod), "Firesky UnderNet Browser", "1.0.0", "Firesky")]
[assembly: MelonGame("TVGS", "Schedule I")]
// WebView2.Core is shipped/resolved from mod subfolders (e.g. Mods/UnderNetBrowser) via runtime assembly resolution.
[assembly: MelonOptionalDependencies("Microsoft.Web.WebView2.Core")]
