// Tool module for `check_model`. Works through the default handler for its op kinds (framework/defaults.ts).
// The owning wave-2 lane adds prepare/handle/finish hooks here when the defaults are not enough.
import type { ToolModule } from "../framework/types.js";

export const module: ToolModule = { name: "check_model" };
