using System;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RevitMcpNext.Addin.Ipc
{
    internal static class PipeSecurityFactory
    {
        public static PipeSecurity CreateCurrentUserOnly()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                SecurityIdentifier user = identity.User;
                if (user == null)
                {
                    throw new InvalidOperationException("Could not determine the current Windows user for named pipe security.");
                }

                var security = new PipeSecurity();
                AddFullControl(security, user);
                AddFullControl(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
                AddFullControl(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
                security.SetOwner(user);
                security.SetGroup(user);
                return security;
            }
        }

        private static void AddFullControl(PipeSecurity security, IdentityReference identity)
        {
            security.AddAccessRule(new PipeAccessRule(
                identity,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
        }
    }
}
