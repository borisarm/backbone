using System.ComponentModel;
using System.Security.Principal;
using Backbone.Service.Os;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>Doble de <see cref="IAccountDirectory"/>: cuentas y pertenencia a Administradores configurables.</summary>
internal sealed class FakeAccountDirectory : IAccountDirectory
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, AccountInfo> _bySid = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _administrators = new(StringComparer.OrdinalIgnoreCase);
    private Exception? _membershipFailure;
    private Exception? _lookupFailure;

    public int MembershipQueries { get; private set; }

    public FakeAccountDirectory AddUser(string sid, string name, bool administrator)
    {
        lock (_gate)
        {
            _bySid[sid] = new AccountInfo(new SecurityIdentifier(sid), name, AccountKind.User);
            SetAdministratorLocked(sid, administrator);
        }

        return this;
    }

    public FakeAccountDirectory AddGroup(string sid, string name)
    {
        lock (_gate)
        {
            _bySid[sid] = new AccountInfo(new SecurityIdentifier(sid), name, AccountKind.Other);
        }

        return this;
    }

    public void SetAdministrator(string sid, bool administrator)
    {
        lock (_gate)
        {
            SetAdministratorLocked(sid, administrator);
        }
    }

    /// <summary>Hace que <see cref="IsLocalAdministrator"/> lance (consulta al sistema fallida).</summary>
    public void FailMembershipQueries(Exception? failure)
    {
        lock (_gate)
        {
            _membershipFailure = failure;
        }
    }

    /// <summary>Hace que las búsquedas de cuentas lancen.</summary>
    public void FailLookups(Exception? failure)
    {
        lock (_gate)
        {
            _lookupFailure = failure;
        }
    }

    public AccountInfo? LookupByName(string qualifiedName)
    {
        lock (_gate)
        {
            if (_lookupFailure is { } failure)
            {
                throw failure;
            }

            return _bySid.Values.FirstOrDefault(a => string.Equals(a.AccountName, qualifiedName, StringComparison.OrdinalIgnoreCase));
        }
    }

    public AccountInfo? LookupBySid(SecurityIdentifier sid)
    {
        lock (_gate)
        {
            if (_lookupFailure is { } failure)
            {
                throw failure;
            }

            return _bySid.GetValueOrDefault(sid.Value);
        }
    }

    public bool IsLocalAdministrator(SecurityIdentifier sid)
    {
        lock (_gate)
        {
            MembershipQueries++;
            if (_membershipFailure is { } failure)
            {
                throw failure;
            }

            return _administrators.Contains(sid.Value);
        }
    }

    public static Win32Exception QueryFailed() => new(5);

    private void SetAdministratorLocked(string sid, bool administrator)
    {
        if (administrator)
        {
            _administrators.Add(sid);
        }
        else
        {
            _administrators.Remove(sid);
        }
    }
}
