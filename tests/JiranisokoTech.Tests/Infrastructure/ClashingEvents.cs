using JiranisokoTech.Domain.Common;

// Two events with the same short name, in different namespaces, existing only
// to prove the clash is caught. Nothing scans this assembly for events — the
// dispatcher tests hand the registry an explicit list — so these cannot break
// anything else.
namespace Warehouse
{
    public sealed record Dispatched(Guid OrderId) : DomainEvent;
}

namespace Invoicing
{
    public sealed record Dispatched(Guid InvoiceId) : DomainEvent;
}
