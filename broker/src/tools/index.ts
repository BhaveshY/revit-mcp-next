// ALL_TOOL_MODULES: one import per catalog tool, in tools/list order. Pre-populated by W1-BROKER; never edited in wave 2
// (lanes edit only their own tool files).
import type { ToolModule } from "../framework/types.js";
import { module as m_status } from "./status.js";
import { module as m_set_target } from "./set_target.js";
import { module as m_list } from "./list.js";
import { module as m_find_elements } from "./find_elements.js";
import { module as m_describe_elements } from "./describe_elements.js";
import { module as m_get_view } from "./get_view.js";
import { module as m_read_schedule } from "./read_schedule.js";
import { module as m_read_family } from "./read_family.js";
import { module as m_check_model } from "./check_model.js";
import { module as m_get_quantities } from "./get_quantities.js";
import { module as m_get_changes } from "./get_changes.js";
import { module as m_read_many } from "./read_many.js";
import { module as m_capture } from "./capture.js";
import { module as m_ui } from "./ui.js";
import { module as m_create_elements } from "./create_elements.js";
import { module as m_place_family } from "./place_family.js";
import { module as m_modify_elements } from "./modify_elements.js";
import { module as m_set_parameters } from "./set_parameters.js";
import { module as m_edit_types } from "./edit_types.js";
import { module as m_edit_views } from "./edit_views.js";
import { module as m_view_graphics } from "./view_graphics.js";
import { module as m_edit_sheets } from "./edit_sheets.js";
import { module as m_annotate } from "./annotate.js";
import { module as m_edit_schedules } from "./edit_schedules.js";
import { module as m_edit_family } from "./edit_family.js";
import { module as m_mep } from "./mep.js";
import { module as m_structure } from "./structure.js";
import { module as m_manage_document } from "./manage_document.js";
import { module as m_worksharing } from "./worksharing.js";
import { module as m_links } from "./links.js";
import { module as m_export } from "./export.js";
import { module as m_model_delivery } from "./model_delivery.js";
import { module as m_change_set } from "./change_set.js";
import { module as m_undo } from "./undo.js";
import { module as m_job_status } from "./job_status.js";
import { module as m_cancel_job } from "./cancel_job.js";
import { module as m_help } from "./help.js";
import { module as m_run_csharp } from "./run_csharp.js";

export const ALL_TOOL_MODULES: readonly ToolModule[] = Object.freeze([
  m_status,
  m_set_target,
  m_list,
  m_find_elements,
  m_describe_elements,
  m_get_view,
  m_read_schedule,
  m_read_family,
  m_check_model,
  m_get_quantities,
  m_get_changes,
  m_read_many,
  m_capture,
  m_ui,
  m_create_elements,
  m_place_family,
  m_modify_elements,
  m_set_parameters,
  m_edit_types,
  m_edit_views,
  m_view_graphics,
  m_edit_sheets,
  m_annotate,
  m_edit_schedules,
  m_edit_family,
  m_mep,
  m_structure,
  m_manage_document,
  m_worksharing,
  m_links,
  m_export,
  m_model_delivery,
  m_change_set,
  m_undo,
  m_job_status,
  m_cancel_job,
  m_help,
  m_run_csharp,
]);
