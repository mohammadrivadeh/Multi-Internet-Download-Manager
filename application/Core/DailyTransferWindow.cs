using System.Globalization;
namespace DownloadManager.Core;

/// <summary>A recurring daily window at an explicit fixed UTC offset; start inclusive, end exclusive.</summary>
public sealed record DailyTransferWindow(int StartMinute, int EndMinute, int OffsetMinutes)
{
    public void Validate()
    {
        if (StartMinute is < 0 or >= 1440 || EndMinute is < 0 or >= 1440 || StartMinute == EndMinute || OffsetMinutes is < -840 or > 840)
            throw new ArgumentException("Invalid daily window.");
    }
    public bool IsOpen(DateTimeOffset now)
    {
        Validate();
        var local = now.ToOffset(TimeSpan.FromMinutes(OffsetMinutes));
        var minute = local.Hour * 60 + local.Minute;
        return StartMinute < EndMinute ? minute >= StartMinute && minute < EndMinute : minute >= StartMinute || minute < EndMinute;
    }
    public TimeSpan Remaining(DateTimeOffset now)
    {
        if (!IsOpen(now)) return TimeSpan.Zero;
        var local = now.ToOffset(TimeSpan.FromMinutes(OffsetMinutes));
        var finish = new DateTimeOffset(local.Date, local.Offset).AddMinutes(EndMinute);
        if (finish <= local) finish = finish.AddDays(1);
        return finish - local;
    }
    public static DailyTransferWindow? Parse(string start, string end, string offset)
    {
        if (string.IsNullOrWhiteSpace(start) && string.IsNullOrWhiteSpace(end)) return null;
        var from = TimeOnly.ParseExact(start, "HH:mm", CultureInfo.InvariantCulture);
        var to = TimeOnly.ParseExact(end, "HH:mm", CultureInfo.InvariantCulture);
        var zone = DateTimeOffset.ParseExact("2000-01-01T00:00:00" + offset, "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture).Offset;
        var value = new DailyTransferWindow(from.Hour * 60 + from.Minute, to.Hour * 60 + to.Minute, (int)zone.TotalMinutes);
        value.Validate(); return value;
    }
}
public sealed class TransferWindowClosedException : OperationCanceledException;
