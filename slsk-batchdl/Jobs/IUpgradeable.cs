using System.Collections.Generic;

namespace Jobs
{
    public interface IUpgradeable
    {
        IEnumerable<Job> Upgrade(bool album, bool aggregate);
    }
}
