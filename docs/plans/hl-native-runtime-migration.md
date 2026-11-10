# HL 原生库接入 Runtime

## 要解决的问题

HL 原生库现有的任务调度是 Runtime 的前身。Task 的排队、接收、取消和退出策略都放进 Runtime 以后，HL 如果不改，就会有两套重叠的机制。HL 要跟着做一次重构，哪边的做法更好就按哪边来。

## 目前的分工设想

| 职责 | HL 现在 | 重构后 |
|---|---|---|
| 每个任务一个专用线程 | HL 自己开 | 去掉，由 Runtime 执行 |
| 按键排队、排队时送 `queued` | HL 自己的队列 | Runtime 排队；HL 只决定用什么当键，并把状态转成 `phase:"queued"` |
| 退出策略 `Wait`／`CancelOnExit` | 登记 op 时声明 | 写在 Task 类里 |
| 预退出、撤销退出、shutdown | HL 自己的状态 | 分别调用 `PreStopAsync`、`CancelPreStop`、`StopAsync` |
| 取消 | HL 触发取消令牌 | `hl_cancel` 调用 `run.RequestCancellation()` |
| C 接口导出、JSON 解析和错误码 | HL | 仍是 HL |
| 进度节流、事件队列、pump、ACK | HL | 仍是 HL，做成输出接收端 |
| job id 和任务的对应、`hl_list` | HL | 仍是 HL，记录 job id 和 `TaskRun` 的对应关系 |

重构以后，`hl_start` 大致是：把 op 名换成对应的 Task 类，把 JSON 转成输入，建一个输出接收端，然后调用 `Submit`。

## Native 方案讨论中得出的结论

[Haytham.Runtime.Native 方案](runtime-native.md)已经暂缓，但讨论中有几条结论和通用接口无关，HL 接入时同样适用：

- HL 的后端项目直接引用 Runtime 和模块包，在 C# 里调用 Task 的强类型 `Submit`，不经过自己导出的 C 函数再绕回 C#。
- `Submit` 在接纳登记后立刻返回，不等业务执行，所以 `hl_start` 可以保持同步返回 job id。
- HL 的任务记录在调用 `Submit` 之前就准备好，输出接收端直接持有这条记录，`Submit` 成功后再补上 `TaskRun`。这样任务很快产生输出时也能找到记录；接纳失败时删掉这条还没有公开的记录。
- 完成事件必须是这个任务的最后一条事件。
- 单个任务的结果编码失败时，发一条带输出失败标记的完成事件，不终止进程；业务成功仍然保存在 Runtime 的结果里，不改称失败，也不用空结果冒充。只有事件通道本身出故障时才终止进程。
- `Dart_PostCObject` 返回 false 时按接收端已关闭处理，不终止进程，沿用 HL 现有的暂停和重连规则。
- Runtime 的 Start、Stop 要等待业务和清理，所以 `hl_init`、`hl_shutdown` 要改成异步等待，不在 Dart 主 isolate 上同步等。
- 把 Runtime 的日志接到 HL 的发行版之前，先处理[发行版日志里的异常详情和脱敏](log-redaction.md)。现在发行版日志只有类型名和计数，看不到异常消息和堆栈。
- 安全退出的顺序是：PreStop 并等结果，等全部任务的完成事件处理完，发起 Stop，检查 `StopOutcome` 和输出故障，最后关闭事件通道。Stop 被否决、判定失败或清理失败时，不能关闭通道后报告退出成功。

## 为什么现在不做

要等 Runtime 第一版实现完成。具体怎么改 HL，到时候再单独规划。

2026-11-07 起，具体的接入规划由 Haytham Launcher 仓库接手。
