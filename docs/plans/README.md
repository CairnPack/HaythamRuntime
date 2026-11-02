# 暂缓的提案

这里放讨论过、用户认为有价值，但第一版先不做的设计。每份文件是一个提案，写明它解决什么问题、目前想到的做法、为什么现在不做，以及什么时候该拿出来。需要时从这里取出，再按当时的需求重新设计和评审。

来源都是 [Event 架构设计评审](../event-architecture-review.md)（下称“评审文件”）。

| 提案 | 什么时候拿出来 |
|---|---|
| [任务并发上限](task-concurrency-limits.md) | 同时运行的任务太多、影响机器或界面响应时 |
| [后台任务的加权调度](weighted-background-scheduling.md) | 做了并发上限，并且后台任务和用户任务开始抢名额时 |
| [按条件订阅：OnSubscribing 阶段](conditional-subscription-phase.md) | 出现“某个事件存在才订阅”的需求时 |
| [用特性标记订阅者](attribute-based-subscription.md) | 模块变多、手写 `Subscribe` 调用变得繁琐时 |
| [OnStopRequested 的否决语义](stop-request-veto.md) | 开始细化停止流程时 |
| [HL 原生库接入 Runtime](hl-native-runtime-migration.md) | Runtime 第一版实现完成、准备接入 HL 时 |
