using Microsoft.VisualStudio.TextManager.Interop;
using System;

namespace ShaderTools.VisualStudio.Editor
{
    internal static class ShaderNavigationBarRegistration
    {
        internal static bool TryRemoveOwned(IVsDropdownBarManager manager, IVsDropdownBarClient client,
            IVsDropdownBar expectedBar)
        {
            if (expectedBar == null || manager.GetDropdownBar(out IVsDropdownBar current) < 0 ||
                !Equals(current, expectedBar) || current.GetClient(out IVsDropdownBarClient owner) < 0 ||
                !ReferenceEquals(owner, client))
            {
                return false;
            }

            return manager.RemoveDropdownBar() >= 0;
        }

        internal static bool TryInstall(IVsDropdownBarManager manager, IVsDropdownBarClient client,
            out IVsDropdownBar bar)
        {
            bar = null!;
            if (manager.GetDropdownBar(out IVsDropdownBar existing) < 0)
            {
                return false;
            }

            if (existing != null)
            {
                if (existing.GetClient(out IVsDropdownBarClient owner) < 0 || owner == null)
                {
                    return false;
                }

                if (ReferenceEquals(owner, client))
                {
                    bar = existing;
                    return true;
                }
                // VS installs its generic LSP bar before shader navigation on warm editor
                // activation. It has no shader-specific entries. Replace only that known
                // provider; unknown owners remain untouched if VS changes its implementation.
                Type type = owner.GetType();
                if (type.FullName != "Microsoft.VisualStudio.LanguageServer.Client.RemoteLanguageDropdownBarClient" ||
                    type.Assembly.GetName().Name != "Microsoft.VisualStudio.LanguageServer.Client.Implementation")
                {
                    return false;
                }

                if (manager.RemoveDropdownBar() < 0)
                {
                    return false;
                }
            }
            if (manager.AddDropdownBar(2, client) < 0)
            {
                return false;
            }

            return manager.GetDropdownBar(out bar) >= 0 && bar != null;
        }
    }
}
