using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>同じ機能の複数moduleが必要とする入力・観測だけを共用します。</summary>
internal static class StartupProgressTestSupport
{

    /// <remarks>分割先のケースが同じ入力・観測を共用します。モデル・Task・媒体の所有と終端は元の呼出元・fixtureに従います。</remarks>
    internal static StartupProgressWorkflowOwner Start(StartupProgressOperationKind operationKind)
    {

        StartupProgressWorkflowOwner owner = TestStartupProgressOwnerFactory.Create();
        owner.StartStartupProgressOperation(operationKind);
        return owner;
    }

}
