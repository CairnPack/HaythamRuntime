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

## 为什么现在不做

要等 Runtime 第一版实现完成。具体怎么改 HL，到时候再单独规划。
