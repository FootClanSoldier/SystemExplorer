@tool
extends EditorPlugin

# External automated Tree selection stress for System Explorer.
#
# This is intentionally not exact human mouse-input simulation. It runs outside
# System Explorer's managed C# plugin lifecycle and only drives its live Tree via
# TreeItem.select(0). The selectable cadence profiles are diagnostic stress
# stimuli, not guarantees that any specific navigation reaches indexing.

enum NavigationProfile {
	NORMAL,
	INDEXING_EDGE,
	INDEXING_SWEET_SPOT,
	INDEXING_MARGIN,
}

const NORMAL_NAVIGATION_INTERVAL_SECONDS := 0.075
const INDEXING_EDGE_NAVIGATION_INTERVAL_SECONDS := 0.45
const INDEXING_SWEET_SPOT_NAVIGATION_INTERVAL_SECONDS := 0.55
const INDEXING_MARGIN_NAVIGATION_INTERVAL_SECONDS := 0.60
const NAVIGATION_PROFILE_SETTING := "system_explorer_navigation_stress/navigation_profile"
const NAVIGATION_PROFILE_HINT := "Normal (75 ms),Indexing Edge (450 ms),Indexing Sweet Spot (550 ms),Indexing Margin (600 ms)"
const PROGRESS_INTERVAL := 100
const SYSTEM_EXPLORER_DOCK_TITLE := "System Explorer"
const SYSTEM_EXPLORER_CONTENT_NAME := "System Explorer"
const SCRIPT_METADATA_PREFIX := "script::"
const PAUSE_TREE_UNAVAILABLE := "SystemExplorerTreeUnavailable"
const PAUSE_INSUFFICIENT_TARGETS := "InsufficientVisibleScriptTargets"
const LOG_PREFIX := "[SystemExplorerNavigationStress]"

var _accumulator_seconds: float = 0.0
var _current_navigation_profile: int = NavigationProfile.NORMAL
var _current_navigation_interval_seconds: float = NORMAL_NAVIGATION_INTERVAL_SECONDS
var _next_target_index: int = 0
var _successful_selection_count: int = 0
var _last_pause_reason: String = ""
var _system_explorer_tree: Tree = null


func _enter_tree() -> void:
	_reset_local_diagnostic_state()
	_register_navigation_profile_setting()
	_apply_navigation_profile_from_editor_settings(false)
	set_process(true)
	print(
		LOG_PREFIX,
		" External navigation stress started Profile='",
		_get_navigation_profile_name(_current_navigation_profile),
		"' IntervalMs=",
		_get_current_interval_milliseconds()
	)


func _exit_tree() -> void:
	set_process(false)
	print(
		LOG_PREFIX,
		" External navigation stress stopped Selections=",
		_successful_selection_count,
		" Profile='",
		_get_navigation_profile_name(_current_navigation_profile),
		"' IntervalMs=",
		_get_current_interval_milliseconds()
	)
	_reset_local_diagnostic_state()


func _process(delta: float) -> void:
	if _apply_navigation_profile_from_editor_settings(true):
		# Discard this turn's delta as well as the previous accumulator so no elapsed
		# time from the old cadence can contribute to the new profile's first window.
		return

	if delta > 0.0:
		_accumulator_seconds += delta

	if _accumulator_seconds < _current_navigation_interval_seconds:
		return

	# Never catch up after a long frame. One process turn can perform at most one
	# TreeItem.select(0), and the next interval starts cleanly from this frame.
	_accumulator_seconds = 0.0
	_execute_navigation_step()


func _register_navigation_profile_setting() -> void:
	var editor_settings := EditorInterface.get_editor_settings()
	if editor_settings == null:
		return

	if not editor_settings.has_setting(NAVIGATION_PROFILE_SETTING):
		editor_settings.set_setting(NAVIGATION_PROFILE_SETTING, NavigationProfile.NORMAL)

	editor_settings.set_initial_value(
		NAVIGATION_PROFILE_SETTING,
		NavigationProfile.NORMAL,
		false
	)
	editor_settings.add_property_info({
		"name": NAVIGATION_PROFILE_SETTING,
		"type": TYPE_INT,
		"hint": PROPERTY_HINT_ENUM,
		"hint_string": NAVIGATION_PROFILE_HINT,
	})


func _apply_navigation_profile_from_editor_settings(log_change: bool) -> bool:
	var resolved_profile := NavigationProfile.NORMAL
	var editor_settings := EditorInterface.get_editor_settings()
	if editor_settings != null and editor_settings.has_setting(NAVIGATION_PROFILE_SETTING):
		resolved_profile = _resolve_navigation_profile(
			editor_settings.get_setting(NAVIGATION_PROFILE_SETTING)
		)

	if resolved_profile == _current_navigation_profile:
		_current_navigation_interval_seconds = _get_navigation_interval_seconds(resolved_profile)
		return false

	_current_navigation_profile = resolved_profile
	_current_navigation_interval_seconds = _get_navigation_interval_seconds(resolved_profile)
	_accumulator_seconds = 0.0

	if log_change:
		print(
			LOG_PREFIX,
			" External navigation stress profile changed Profile='",
			_get_navigation_profile_name(_current_navigation_profile),
			"' IntervalMs=",
			_get_current_interval_milliseconds()
		)

	return true


func _resolve_navigation_profile(value: Variant) -> int:
	if typeof(value) != TYPE_INT:
		return NavigationProfile.NORMAL

	var profile := int(value)
	match profile:
		NavigationProfile.NORMAL:
			return profile
		NavigationProfile.INDEXING_EDGE:
			return profile
		NavigationProfile.INDEXING_SWEET_SPOT:
			return profile
		NavigationProfile.INDEXING_MARGIN:
			return profile
		_:
			return NavigationProfile.NORMAL


func _get_navigation_interval_seconds(profile: int) -> float:
	match profile:
		NavigationProfile.INDEXING_EDGE:
			return INDEXING_EDGE_NAVIGATION_INTERVAL_SECONDS
		NavigationProfile.INDEXING_SWEET_SPOT:
			return INDEXING_SWEET_SPOT_NAVIGATION_INTERVAL_SECONDS
		NavigationProfile.INDEXING_MARGIN:
			return INDEXING_MARGIN_NAVIGATION_INTERVAL_SECONDS
		NavigationProfile.NORMAL:
			return NORMAL_NAVIGATION_INTERVAL_SECONDS
		_:
			return NORMAL_NAVIGATION_INTERVAL_SECONDS


func _get_navigation_profile_name(profile: int) -> String:
	match profile:
		NavigationProfile.INDEXING_EDGE:
			return "Indexing Edge"
		NavigationProfile.INDEXING_SWEET_SPOT:
			return "Indexing Sweet Spot"
		NavigationProfile.INDEXING_MARGIN:
			return "Indexing Margin"
		NavigationProfile.NORMAL:
			return "Normal"
		_:
			return "Normal"


func _get_current_interval_milliseconds() -> int:
	return roundi(_current_navigation_interval_seconds * 1000.0)


func _execute_navigation_step() -> void:
	var tree := _resolve_system_explorer_tree()
	if tree == null:
		_set_pause_reason(PAUSE_TREE_UNAVAILABLE)
		return

	if _last_pause_reason == PAUSE_TREE_UNAVAILABLE:
		_clear_pause_reason()

	var root := tree.get_root()
	if root == null or not is_instance_valid(root):
		_clear_cached_tree()
		_set_pause_reason(PAUSE_TREE_UNAVAILABLE)
		return

	var targets: Array[TreeItem] = []
	var current := root.get_first_child()
	while current != null:
		if not is_instance_valid(current):
			_clear_cached_tree()
			_set_pause_reason(PAUSE_TREE_UNAVAILABLE)
			return

		var metadata := current.get_metadata(0)
		if typeof(metadata) == TYPE_STRING and String(metadata).begins_with(SCRIPT_METADATA_PREFIX):
			targets.append(current)

		current = current.get_next_visible(false)

	if targets.size() < 2:
		_set_pause_reason(PAUSE_INSUFFICIENT_TARGETS)
		return

	_clear_pause_reason()

	var selected := tree.get_selected()
	var target_count := targets.size()
	var start_index := posmod(_next_target_index, target_count)

	for offset in range(target_count):
		var candidate_index := (start_index + offset) % target_count
		var candidate: TreeItem = targets[candidate_index]

		if candidate == null or not is_instance_valid(candidate):
			continue
		if candidate == selected:
			continue
		if candidate.get_tree() != tree:
			continue

		# Capture any progress-log data before selection. System Explorer's synchronous
		# ItemSelected path may rebuild or otherwise mutate editor/tree state.
		var target_metadata := String(candidate.get_metadata(0))
		_next_target_index = (candidate_index + 1) % target_count

		candidate.select(0)
		_successful_selection_count += 1

		if _successful_selection_count % PROGRESS_INTERVAL == 0:
			print(
				LOG_PREFIX,
				" External navigation stress progress Selections=",
				_successful_selection_count,
				" TargetMetadata='",
				target_metadata,
				"' Profile='",
				_get_navigation_profile_name(_current_navigation_profile),
				"' IntervalMs=",
				_get_current_interval_milliseconds()
			)

		return

	_set_pause_reason(PAUSE_TREE_UNAVAILABLE)


func _resolve_system_explorer_tree() -> Tree:
	if _is_cached_tree_current():
		return _system_explorer_tree

	_clear_cached_tree()

	var base_control := EditorInterface.get_base_control()
	if base_control == null or not is_instance_valid(base_control) or not base_control.is_inside_tree():
		return null

	var docks: Array[EditorDock] = []
	_collect_system_explorer_editor_docks(base_control, docks)
	if docks.size() != 1:
		return null

	var tree := _find_tree_in_system_explorer_dock(docks[0])
	if tree == null:
		return null
	if not is_instance_valid(tree) or not tree.is_inside_tree():
		return null

	_system_explorer_tree = tree
	return _system_explorer_tree


func _is_cached_tree_current() -> bool:
	return (
		_system_explorer_tree != null
		and is_instance_valid(_system_explorer_tree)
		and _system_explorer_tree.is_inside_tree()
	)


func _clear_cached_tree() -> void:
	_system_explorer_tree = null


func _collect_system_explorer_editor_docks(node: Node, matches: Array[EditorDock]) -> void:
	if node == null or not is_instance_valid(node):
		return

	if node is EditorDock:
		var editor_dock := node as EditorDock
		if editor_dock.title == SYSTEM_EXPLORER_DOCK_TITLE:
			matches.append(editor_dock)

	for child in node.get_children():
		if child is Node:
			_collect_system_explorer_editor_docks(child, matches)


func _find_tree_in_system_explorer_dock(editor_dock: EditorDock) -> Tree:
	if editor_dock == null or not is_instance_valid(editor_dock):
		return null

	var content_roots: Array[VBoxContainer] = []
	for child in editor_dock.get_children():
		if child is VBoxContainer and child.name == SYSTEM_EXPLORER_CONTENT_NAME:
			content_roots.append(child as VBoxContainer)

	if content_roots.size() != 1:
		return null

	var direct_trees: Array[Tree] = []
	for child in content_roots[0].get_children():
		if child is Tree:
			direct_trees.append(child as Tree)

	if direct_trees.size() != 1:
		return null

	return direct_trees[0]


func _set_pause_reason(reason: String) -> void:
	if _last_pause_reason == reason:
		return

	_last_pause_reason = reason
	print(
		LOG_PREFIX,
		" External navigation stress paused Reason=",
		reason
	)


func _clear_pause_reason() -> void:
	if _last_pause_reason.is_empty():
		return

	var previous_reason := _last_pause_reason
	_last_pause_reason = ""
	print(
		LOG_PREFIX,
		" External navigation stress resumed PreviousReason=",
		previous_reason
	)


func _reset_local_diagnostic_state() -> void:
	_clear_cached_tree()
	_accumulator_seconds = 0.0
	_current_navigation_profile = NavigationProfile.NORMAL
	_current_navigation_interval_seconds = NORMAL_NAVIGATION_INTERVAL_SECONDS
	_next_target_index = 0
	_successful_selection_count = 0
	_last_pause_reason = ""
