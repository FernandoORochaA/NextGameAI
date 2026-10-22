using Microsoft.EntityFrameworkCore;
using NextGameAI.Web.Data;

namespace NextGameAI.Web.Services;

public class CurrentUserProfile(NextGameAiDbContext db)
{
    public Task<int> GetIdAsync(CancellationToken cancellationToken = default) =>
        db.UserProfiles.Where(x => x.Id == LocalProfile.Id).Select(x => x.Id).SingleAsync(cancellationToken);
}
