using Ripcord.Domain.Updates;

namespace Ripcord.Ports.Updates;

/// What the last look found, across process boundaries. Nothing on these hosts looks on its
/// own and no command looks while it runs, so the only way `status` can mention a release is
/// for something earlier to have written it down.
///
/// Read never throws. A file that cannot be read is a host that has not looked — the cost is
/// a line nobody sees, where a throw would be `ripcord status` failing during an incident on
/// a file nobody knew existed. The same reasoning as `IAlertStateStore`, for the same reason.
public interface IUpdateNoticeStore
{
    UpdateNotice? Read();

    void Write(UpdateNotice notice);
}
