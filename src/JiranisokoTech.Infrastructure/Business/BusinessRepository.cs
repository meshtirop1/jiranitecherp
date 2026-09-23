using JiranisokoTech.Application.Business;
using JiranisokoTech.Domain.Clients;
using JiranisokoTech.Domain.Money;
using JiranisokoTech.Domain.Recruitment;
using JiranisokoTech.Domain.Time;
using JiranisokoTech.Domain.Work;
using JiranisokoTech.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiranisokoTech.Infrastructure.Business;

public sealed class BusinessRepository(AppDbContext database) : IBusinessRepository
{
    public Task<Client?> FindClientAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.Clients.FirstOrDefaultAsync(client => client.Id == id, cancellationToken);

    public Task<bool> ClientCodeTakenAsync(
        string code, CancellationToken cancellationToken = default) =>
        database.Clients.AnyAsync(client => client.Code == code, cancellationToken);

    public Task<int> LiveProjectsForAsync(
        Guid clientId, CancellationToken cancellationToken = default) =>
        database.Projects.CountAsync(
            project => project.ClientId == clientId
                && project.Status != ProjectStatus.Delivered
                && project.Status != ProjectStatus.Cancelled,
            cancellationToken);

    /// <summary>
    /// Loaded with its lines and payments, always.
    /// </summary>
    /// <remarks>
    /// Every total on an invoice is summed from them, so one loaded without
    /// them reports a total of zero — which is not an error anywhere, just a
    /// wrong number on a document.
    /// </remarks>
    public Task<Invoice?> FindInvoiceAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.Invoices
            .Include(invoice => invoice.Lines)
            .Include(invoice => invoice.Payments)
            .FirstOrDefaultAsync(invoice => invoice.Id == id, cancellationToken);

    public Task<bool> InvoiceNumberTakenAsync(
        string number, CancellationToken cancellationToken = default) =>
        database.Invoices.AnyAsync(invoice => invoice.Number == number, cancellationToken);

    public async Task<int> LastInvoiceSequenceAsync(
        string firmPrefix, int year, CancellationToken cancellationToken = default)
    {
        var prefix = $"{firmPrefix}-{year}-";

        var numbers = await database.Invoices
            .AsNoTracking()
            .Where(invoice => invoice.Number.StartsWith(prefix))
            .Select(invoice => invoice.Number)
            .ToListAsync(cancellationToken);

        // Parsed here rather than in SQL: the format is ours, the list is one
        // year of invoices, and a provider-specific substring expression would
        // be the only thing in this file that could not run on both databases.
        return numbers
            .Select(number => int.TryParse(number[prefix.Length..], out var sequence) ? sequence : 0)
            .DefaultIfEmpty(0)
            .Max();
    }

    public Task<ExpenseClaim?> FindClaimAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        database.Expenses.FirstOrDefaultAsync(claim => claim.Id == id, cancellationToken);

    public Task<TimeEntry?> FindTimeAsync(Guid id, CancellationToken cancellationToken = default) =>
        database.TimeEntries.FirstOrDefaultAsync(entry => entry.Id == id, cancellationToken);

    public async Task<int> MinutesOnAsync(
        Guid employeeId,
        DateOnly on,
        Guid? except = null,
        CancellationToken cancellationToken = default) =>
        await database.TimeEntries
            .Where(entry => entry.EmployeeId == employeeId && entry.On == on)
            .Where(entry => except == null || entry.Id != except)
            .SumAsync(entry => entry.Minutes, cancellationToken);

    public Task<List<TimeEntry>> BillableTimeAsync(
        Guid projectId, CancellationToken cancellationToken = default) =>
        database.TimeEntries
            .Where(entry => entry.ProjectId == projectId
                && entry.IsBillable
                && entry.ApprovedAt != null
                && entry.InvoiceId == null)
            .OrderBy(entry => entry.On)
            .ToListAsync(cancellationToken);

    public Task<LeaveRequest?> FindLeaveAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        database.Leave.FirstOrDefaultAsync(leave => leave.Id == id, cancellationToken);

    public Task<List<LeaveRequest>> LeaveOverlappingAsync(
        Guid employeeId,
        DateOnly from,
        DateOnly to,
        Guid? except = null,
        CancellationToken cancellationToken = default) =>
        database.Leave
            .Where(leave => leave.EmployeeId == employeeId)
            .Where(leave => leave.Status == LeaveStatus.Draft
                || leave.Status == LeaveStatus.AwaitingApproval
                || leave.Status == LeaveStatus.Approved)
            // Two ranges overlap when each starts before the other ends.
            .Where(leave => leave.From <= to && from <= leave.To)
            .Where(leave => except == null || leave.Id != except)
            .ToListAsync(cancellationToken);

    public Task<Holiday?> FindHolidayAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        database.Holidays.FirstOrDefaultAsync(holiday => holiday.Id == id, cancellationToken);

    public Task<bool> HolidayTakenAsync(
        DateOnly on, CancellationToken cancellationToken = default) =>
        database.Holidays.AnyAsync(holiday => holiday.On == on, cancellationToken);

    public async Task<IReadOnlySet<DateOnly>> HolidaysAsync(
        CancellationToken cancellationToken = default) =>
        (await database.Holidays
            .AsNoTracking()
            .Select(holiday => holiday.On)
            .ToListAsync(cancellationToken))
        .ToHashSet();

    public Task<List<LeaveRequest>> LiveLeaveSpanningAsync(
        DateOnly on, CancellationToken cancellationToken = default) =>
        database.Leave
            .Where(leave => leave.From <= on && on <= leave.To)
            .Where(leave => leave.Status == LeaveStatus.Draft
                || leave.Status == LeaveStatus.AwaitingApproval
                || leave.Status == LeaveStatus.Approved)
            .ToListAsync(cancellationToken);

    public Task<Interview?> FindInterviewAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        database.Interviews
            .Include(interview => interview.Panel)
            .Include(interview => interview.Scorecards)
            .FirstOrDefaultAsync(interview => interview.Id == id, cancellationToken);

    public Task<JobApplication?> FindApplicationAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        database.Applications.FirstOrDefaultAsync(
            application => application.Id == id, cancellationToken);

    public void Add(Client client) => database.Clients.Add(client);

    public void Add(Invoice invoice) => database.Invoices.Add(invoice);

    public void Add(ExpenseClaim claim) => database.Expenses.Add(claim);

    public void Add(TimeEntry entry) => database.TimeEntries.Add(entry);

    public void Add(LeaveRequest leave) => database.Leave.Add(leave);

    public void Add(Interview interview) => database.Interviews.Add(interview);

    public void Add(Holiday holiday) => database.Holidays.Add(holiday);

    public void Remove(Holiday holiday) => database.Holidays.Remove(holiday);

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        database.SaveChangesAsync(cancellationToken);
}
