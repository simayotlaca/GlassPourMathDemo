# More Lives — clean asset map

This folder contains only the newly authored, standalone transparent sprites. Detached
micro alpha artifacts were removed and transparent padding was tightly cropped on every
sprite. The card is wired through `BartenderMainMenuCanvas.prefab`.

## New sprites

- `MoreLivesClean_Title_TR.png` — `DAHA FAZLA CAN`
- `MoreLivesClean_Text_NextLife_TR.png` — `SONRAKİ CANA KALAN SÜRE`
- `MoreLivesClean_Button_Refill_Green.png` — empty green refill button; detached alpha artifacts removed
- `MoreLivesClean_Button_Rewarded_Orange.png` — empty orange rewarded button; detached alpha artifacts removed
- `MoreLivesClean_Text_Refill_TR.png` — `DOLDUR`
- `MoreLivesClean_Text_RewardedOneLife_TR.png` — `+1 CAN`

## Shared project sprites to reuse

- Frame: `Art/ResultPopup/Runtime/ResultPopup_Frame_UserApproved.png`
- Heart: `Art/MainMenu/Hud/HUD_Heart.png`
- Currency (mandatory): `Art/MainMenu/Hud/HUD_CoinCocktail.png`
- Clock: `Resources/Ui/OrderTimer/Ui_OrderTimerClock.png`
- Rewarded video: `Art/ResultPopup/Runtime/ResultPopup_RewardedVideo_Icon.png`
- Close base/X: `Art/ResultPopup/Runtime/ResultPopup_CloseButton_Base.png` and
  `ResultPopup_CloseButton_X.png`
- Heart rays: `Art/ResultPopup/Runtime/ResultPopup_CoinBurstRays.png`
- Balance frame: `Art/MainMenu/Hud/HUD_ResourceFrame_CreamGold_Thick.png`

Do not use `ResultPopup_Coin_Small.png` on this card. The count, countdown, cost (`900`),
current balance, and feedback remain dynamic Unity text.
