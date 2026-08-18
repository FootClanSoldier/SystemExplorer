@tool
extends EditorPlugin

# External automated Tree selection stress for System Explorer.
#
# This is intentionally not exact human mouse-input simulation. It runs outside
# System Explorer's managed C# plugin lifecycle and only drives its live Tree via
# TreeItem.select(0). The 75 ms cadence is deliberately far more aggressive than
# normal user navigation and should be interpreted as diagnostic stress stimulus.

const NAVIGATION_INTERVAL_SECONDS := 0.075
const PROGRESS_INTERVAL := 100
const SYSTEM_EXPLORER_DOCK_TITLE := "System Explorer"
const SYSTEM_EXPLORER_CONTENT_NAME := "System Explorer"
const SCRIPT_METADATA_PREFIX := "script::"
const PAUSE_TREE_UNAVAILABLE := "SystemExplorerTreeUnavailable"
const PAUSE_INSUFFICIENT_TARGETS := "InsufficientVisibleScriptTargets"
const LOG_PREFIX := "[SystemExplorerNavigationStress]"

var _accumulator_seconds: float = 0.0
var _next_target_index: int = 0
var _successful_selection_count: int = 0
var _last_pause_reason: String = ""
var _system_explorer_tree: Tree = null


func _enter_tree() -> void:
	_reset_local_diagnostic_state()
	set_process(true)
	print(
		LOG_PREFIX,
		" External navigation stress started IntervalMs=75"
	)


func _exit_tree() -> void:
	set_process(false)
	print(
		LOG_PREFIX,
		" External navigation stress stopped Selections=",
		_successful_selection_count
	)
	_reset_local_diagnostic_state()


func _process(delta: float) -> void:
	if delta > 0.0:
		_accumulator_seconds += delta

	if _accumulator_seconds < NAVIGATION_INTERVAL_SECONDS:
		return

	# Never catch up after a long frame. One process turn can perform at most one
	# TreeItem.select(0), and a new 75 ms interval starts from this frame.
	_accumulator_seconds = 0.0
	_execute_navigation_step()


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
				"'"
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
	_next_target_index = 0
	_successful_selection_count = 0
	_last_pause_reason = ""
