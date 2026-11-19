using System.Reflection;
using System.Reflection.Emit;
using Microsoft.VisualStudio.TextManager.Interop;
using ShaderTools.VisualStudio.Editor;
using Xunit;

namespace ShaderTools.Tests;

public sealed class NavigationBarRegistrationTests
{
    [Fact]
    public void DefaultLspBarIsReplacedForTheShaderClient()
    {
        var host = new BarManager(Client("Microsoft.VisualStudio.LanguageServer.Client.Implementation",
            "Microsoft.VisualStudio.LanguageServer.Client.RemoteLanguageDropdownBarClient"));
        var shader = Client("ShaderTools", "ShaderClient");

        Assert.True(ShaderNavigationBarRegistration.TryInstall(host, shader, out var bar));
        Assert.Same(shader, host.Current!.Client);
        Assert.Same(host.Current, bar);
        Assert.Equal(1, host.Removals);
        Assert.Equal(1, host.Additions);
        Assert.Equal(2, host.ComboCount);
    }

    [Theory]
    [InlineData("OtherExtension", "Microsoft.VisualStudio.LanguageServer.Client.RemoteLanguageDropdownBarClient")]
    [InlineData("Microsoft.VisualStudio.LanguageServer.Client.Implementation", "OtherDropdownBarClient")]
    public void UnrelatedProvidersArePreserved(string assembly, string type)
    {
        var host = new BarManager(Client(assembly, type));
        var original = host.Current;

        Assert.False(ShaderNavigationBarRegistration.TryInstall(host, Client("ShaderTools", "ShaderClient"), out _));
        Assert.Same(original, host.Current);
        Assert.Equal(0, host.Removals);
        Assert.Equal(0, host.Additions);
    }

    [Fact]
    public void ReturningToTheSameShaderBarKeepsItsNativeInstance()
    {
        var shader = Client("ShaderTools", "ShaderClient");
        var host = new BarManager(shader);
        var original = host.Current;

        Assert.True(ShaderNavigationBarRegistration.TryInstall(host, shader, out var bar));
        Assert.Same(original, bar);
        Assert.Equal(0, host.Removals);
        Assert.Equal(0, host.Additions);
    }

    [Fact]
    public void ShaderBarReplacedWhileInactiveIsRestoredOnActivation()
    {
        var shader = Client("ShaderTools", "ShaderClient");
        var host = new BarManager(null);
        Assert.True(ShaderNavigationBarRegistration.TryInstall(host, shader, out _));
        host.Current = new Bar(Client("Microsoft.VisualStudio.LanguageServer.Client.Implementation",
            "Microsoft.VisualStudio.LanguageServer.Client.RemoteLanguageDropdownBarClient"));

        Assert.True(ShaderNavigationBarRegistration.TryInstall(host, shader, out var restored));
        Assert.Same(shader, host.Current.Client);
        Assert.Same(host.Current, restored);
        Assert.Equal(1, host.Removals);
        Assert.Equal(2, host.Additions);
    }

    [Fact]
    public void FailedRemovalDoesNotAddAnotherBar()
    {
        var host = new BarManager(Client("Microsoft.VisualStudio.LanguageServer.Client.Implementation",
            "Microsoft.VisualStudio.LanguageServer.Client.RemoteLanguageDropdownBarClient")) { RemoveResult = -1 };
        var original = host.Current;

        Assert.False(ShaderNavigationBarRegistration.TryInstall(host, Client("ShaderTools", "ShaderClient"), out _));
        Assert.Same(original, host.Current);
        Assert.Equal(0, host.Additions);
    }

    [Fact]
    public void DisposingShaderDoesNotRemoveReplacementClientOnSameAdapter()
    {
        var shader = Client("ShaderTools", "ShaderClient");
        var host = new BarManager(shader);
        var original = host.Current!;
        original.Client = Client("OtherExtension", "OtherClient");

        Assert.False(ShaderNavigationBarRegistration.TryRemoveOwned(host, shader, original));
        Assert.Same(original, host.Current);
        Assert.Equal(0, host.Removals);
    }

    [Fact]
    public void DisposingShaderRemovesOnlyItsOwnedBar()
    {
        var shader = Client("ShaderTools", "ShaderClient");
        var host = new BarManager(shader);

        Assert.True(ShaderNavigationBarRegistration.TryRemoveOwned(host, shader, host.Current!));
        Assert.Null(host.Current);
        Assert.Equal(1, host.Removals);
    }

    // The VS default client is internal. A metadata-equivalent COM client exercises the
    // compatibility boundary without loading the full Visual Studio process into xUnit.
    private static IVsDropdownBarClient Client(string assembly, string type)
    {
        var module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assembly), AssemblyBuilderAccess.Run)
            .DefineDynamicModule("Clients");
        var builder = module.DefineType(type, TypeAttributes.Public);
        builder.AddInterfaceImplementation(typeof(IVsDropdownBarClient));
        foreach (var contract in typeof(IVsDropdownBarClient).GetMethods())
        {
            var method = builder.DefineMethod(contract.Name, MethodAttributes.Public | MethodAttributes.Virtual,
                contract.ReturnType, contract.GetParameters().Select(p => p.ParameterType).ToArray());
            var body = method.GetILGenerator();
            body.Emit(OpCodes.Ldc_I4_0);
            body.Emit(OpCodes.Ret);
            builder.DefineMethodOverride(method, contract);
        }
        return (IVsDropdownBarClient)Activator.CreateInstance(builder.CreateType()!)!;
    }

    private sealed class BarManager(IVsDropdownBarClient? owner) : IVsDropdownBarManager
    {
        internal Bar? Current = owner == null ? null : new Bar(owner);
        internal int Additions, Removals, ComboCount, RemoveResult;
        public int GetDropdownBar(out IVsDropdownBar bar) { bar = Current!; return Current == null ? 1 : 0; }
        public int AddDropdownBar(int count, IVsDropdownBarClient client)
        { Additions++; ComboCount = count; Current = new Bar(client); return 0; }
        public int RemoveDropdownBar()
        { Removals++; if (RemoveResult < 0) return RemoveResult; Current = null; return 0; }
    }

    private sealed class Bar(IVsDropdownBarClient client) : IVsDropdownBar
    {
        internal IVsDropdownBarClient Client = client;
        public int GetClient(out IVsDropdownBarClient owner) { owner = Client; return 0; }
        public int Attach(IntPtr parent, int count, IVsDropdownBarClient owner) => throw new NotSupportedException();
        public void Detach() => throw new NotSupportedException();
        public int GetCurrentSelection(int combo, out int index) { index = 0; return 0; }
        public int RefreshCombo(int combo, int index) => 0;
        public int SetCurrentSelection(int combo, int index) => 0;
    }
}
