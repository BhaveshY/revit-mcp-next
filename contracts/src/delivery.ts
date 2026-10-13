// Model delivery recipe (the model_delivery tool's save_recipe payload and the broker recipe store).
// Mirrors the recipe the add-in reads in addin/RevitMcpNext.Addin/Domains/Delivery/ModelDeliveryWorkflow.cs.

export type ModelDeliveryLinkAction = "repath" | "unload" | "remove" | "retain";
export type ModelDeliveryExportFormat = "ifc" | "dwg" | "nwc";

export interface ModelDeliverySource {
  id: string;
  sourcePath: string;
  targetFileName: string;
  role?: string;
  startViewName?: string;
  start3dViewName?: string;
}

export interface ModelDeliveryLinkRule {
  sourceModelId: string;
  matchPath?: string;
  matchFileName?: string;
  action: ModelDeliveryLinkAction;
  targetModelId?: string;
  expectedInstanceCount?: number;
  required?: boolean;
}

export interface ModelDeliveryCleanupPolicy {
  deleteSheets: boolean;
  deleteViews: boolean;
  deleteSchedules: boolean;
  deleteLegends: boolean;
  deleteDraftingViews: boolean;
  deleteViewTemplates: boolean;
  deleteUnusedFilters: boolean;
  removeUnmappedLinks: boolean;
  purgeUnusedPasses: number;
  protectedViewNames: string[];
}

export interface ModelDeliveryExportRequest {
  format: ModelDeliveryExportFormat;
  modelIds?: string[];
  setupName?: string;
  outputSubdirectory?: string;
  viewNames?: string[];
  required: boolean;
}

export interface ModelDeliveryCoordinatePolicy {
  preserveLinkTransforms: true;
  packagedLinkPathType: "relative";
}

export interface ModelDeliveryQaPolicy {
  requireStandalone: true;
  requireNoCentralPath: true;
  requireSourceHashUnchanged: true;
  requireCleanupMatchesPreview: true;
  requireAllRequiredExports: true;
  maxWarnings?: number;
}

export interface ModelDeliveryRecipe {
  projectId: string;
  recipeVersion: string;
  deliveryId: string;
  packageName: string;
  destinationRoot: string;
  sourceModels: ModelDeliverySource[];
  linkRules: ModelDeliveryLinkRule[];
  coordinates: ModelDeliveryCoordinatePolicy;
  cleanup: ModelDeliveryCleanupPolicy;
  exports: ModelDeliveryExportRequest[];
  qa: ModelDeliveryQaPolicy;
}
