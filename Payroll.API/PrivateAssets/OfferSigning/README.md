# Private offer signing asset

`GadAuthrizedSignAndSeal.png` was explicitly supplied and approved for packaging with the API deployment. It is copied unchanged to build/publish output, outside `wwwroot`; it must not be copied into a frontend, public attachment, or public CDN.

Repository and container access must remain restricted: anyone with those artifacts can read this asset. Keep this repository private. If that access is unsuitable, remove the bundled asset and provision a private runtime mount instead.

The file alone does not authorize signing. The configured client and final approver must match an actually approved final workflow task and the approved offer terms/template. Drafts never load this asset.

Default runtime path: `PrivateAssets/OfferSigning/GadAuthrizedSignAndSeal.png` relative to the API executable. `OfferSigning__Uidai__SignaturePath` optionally overrides it (an invalid explicit override fails closed). `OfferSigning__Uidai__ClientId` and `OfferSigning__Uidai__FinalApproverUserId` remain required.
