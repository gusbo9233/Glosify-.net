// Re-exports the pieces of the ACS calling SDK that classroom-call.js uses.
// Built into Glosify/wwwroot/lib/acs/acs-calling.min.js (exposed as window.acs);
// see README.md in this folder for the regen command.
export {
    CallClient,
    LocalVideoStream,
    VideoStreamRenderer,
    Features
} from "@azure/communication-calling";

export { AzureCommunicationTokenCredential } from "@azure/communication-common";
