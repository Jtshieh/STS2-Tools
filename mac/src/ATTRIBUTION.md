Hook design derived from boardengineer/RunReplays, MIT, commit b0d2302ee69bf2ad735e0b6b51aea02408e9ef62: PlayerActionBuffer, CardPlayRecordPatch, HandCardSelectRecordPatch, EventSelectionPatch, ShopRecordPatch, ProceedToNextActRecordPatch. Retained passive entry/acceptance/choice boundaries only. No PatchAll, unlock, force-seed, encounter, replay dispatch, embedded saves, BaseLib or live Steam deployment. Player projection adapted from owner Linux source/Bridge.cs snapshot. Game-derived inspection remains private.


| Addition | Origin |
| --- | --- |
| Synchronous callback scope, internal notification metadata, and input role labels | Project-authored passive recording and validation; MIT. Uses existing original callbacks without changing game rules or consuming RNG. |
| v2 export notification structure checks | Project-authored extension of the existing v2 report; MIT. Structure, adaptation requirements, and replay evidence remain separate. |
