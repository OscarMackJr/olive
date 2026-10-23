namespace Olive.Application;

// D-08: server-side group to tenant map. Callers enforce exactly-one-tenant on the returned set.
public interface IReviewerTenantMap { IReadOnlyCollection<Guid> TenantsFor(IEnumerable<string> groups); }
