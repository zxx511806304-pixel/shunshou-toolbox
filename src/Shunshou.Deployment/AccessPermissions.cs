using System.Security.AccessControl;

namespace Shunshou.Deployment;

/// <summary>Only the access DACL is handled here. Owner, group and SACL are never written.</summary>
internal static class AccessPermissions
{
    internal static string Read(string path, bool directory)
    {
        PathSafety.NoLinks(path);
        FileSystemSecurity security = directory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        return security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
    }

    internal static void Apply(string path, bool directory, string? accessSddl)
    {
        if (accessSddl is null) return;
        PathSafety.NoLinks(path);
        try
        {
            if (directory)
            {
                var security = new DirectorySecurity();
                security.SetSecurityDescriptorSddlForm(accessSddl, AccessControlSections.Access);
                new DirectoryInfo(path).SetAccessControl(security);
            }
            else
            {
                var security = new FileSecurity();
                security.SetSecurityDescriptorSddlForm(accessSddl, AccessControlSections.Access);
                new FileInfo(path).SetAccessControl(security);
            }
            if (!Equivalent(accessSddl, Read(path, directory)))
                throw new IOException("Windows 未能完整保留访问权限。");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            throw new IOException("无法保留此位置的访问权限，原软件未切换：" + path, ex);
        }
    }

    internal static void Validate(string? sddl)
    {
        if (sddl is not null) _ = Fingerprint(sddl);
    }

    internal static bool Equivalent(string? expected, string? actual)
    {
        // Format-1 journals published before DACL capture can recover existing folders without
        // inventing or writing permissions. New snapshots always contain the captured DACL.
        return expected is null || actual is not null && Fingerprint(expected) == Fingerprint(actual);
    }

    private static string Fingerprint(string sddl)
    {
        if (sddl.Length > 256 * 1024) throw new InvalidDataException("访问权限记录过大。");
        RawSecurityDescriptor descriptor;
        try { descriptor = new RawSecurityDescriptor(sddl); }
        catch (Exception ex) when (ex is ArgumentException or SystemException)
        { throw new InvalidDataException("访问权限记录无效。", ex); }
        var protection = descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected) ? "protected:" : "inherited:";
        if (descriptor.DiscretionaryAcl is null) return protection + "null";
        var entries = new List<string>();
        foreach (GenericAce original in descriptor.DiscretionaryAcl)
        {
            var bytes = new byte[original.BinaryLength];
            original.GetBinaryForm(bytes, 0);
            var ace = GenericAce.CreateFromBinaryForm(bytes, 0);
            if (ace is KnownAce known)
            {
                // Windows can expand generic filesystem rights on SetSecurityInfo. Their effective
                // masks are equal; ACE type/order, SID, inheritance/propagation and callback data stay exact.
                var mask = unchecked((uint)known.AccessMask);
                var mapped = mask & 0x0FFFFFFF;
                if ((mask & 0x10000000) != 0) mapped |= 0x001F01FF;
                if ((mask & 0x80000000) != 0) mapped |= 0x00120089;
                if ((mask & 0x40000000) != 0) mapped |= 0x00120116;
                if ((mask & 0x20000000) != 0) mapped |= 0x001200A0;
                known.AccessMask = unchecked((int)mapped);
                ace.GetBinaryForm(bytes, 0);
            }
            entries.Add(Convert.ToBase64String(bytes));
        }
        return protection + string.Join('|', entries);
    }
}
