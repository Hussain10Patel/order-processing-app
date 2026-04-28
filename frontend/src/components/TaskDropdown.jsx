import { useEffect, useMemo, useRef, useState } from "react";

const STORAGE_KEY = "order-processing.tasks";
const priorityOrder = { High: 0, Medium: 1, Low: 2 };

function readStoredTasks() {
  if (typeof window === "undefined") {
    return [];
  }

  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    return raw ? JSON.parse(raw) : [];
  } catch (error) {
    console.error("Failed to read tasks from storage:", error);
    return [];
  }
}

function writeStoredTasks(tasks) {
  if (typeof window === "undefined") {
    return;
  }

  window.localStorage.setItem(STORAGE_KEY, JSON.stringify(tasks));
}

function ChecklistIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true">
      <path
        d="M9.55 18 4.8 13.25l1.4-1.4 3.35 3.35 8.25-8.25 1.4 1.4L9.55 18ZM19 19H5V5h8v2H7v10h10v-4h2v6ZM17 9V7h-2V5h2V3h2v2h2v2h-2v2h-2Z"
        fill="currentColor"
      />
    </svg>
  );
}

function TaskDropdown() {
  const [isOpen, setIsOpen] = useState(false);
  const [taskText, setTaskText] = useState("");
  const [priority, setPriority] = useState("Medium");
  const [tasks, setTasks] = useState(() => readStoredTasks());
  const containerRef = useRef(null);

  useEffect(() => {
    writeStoredTasks(tasks);
  }, [tasks]);

  useEffect(() => {
    if (!isOpen) {
      return undefined;
    }

    function handlePointerDown(event) {
      if (!containerRef.current?.contains(event.target)) {
        setIsOpen(false);
      }
    }

    function handleEscape(event) {
      if (event.key === "Escape") {
        setIsOpen(false);
      }
    }

    document.addEventListener("mousedown", handlePointerDown);
    document.addEventListener("keydown", handleEscape);

    return () => {
      document.removeEventListener("mousedown", handlePointerDown);
      document.removeEventListener("keydown", handleEscape);
    };
  }, [isOpen]);

  const sortedTasks = useMemo(() => {
    return [...tasks].sort((left, right) => {
      const priorityDiff = priorityOrder[left.priority] - priorityOrder[right.priority];
      if (priorityDiff !== 0) {
        return priorityDiff;
      }

      if (left.completed !== right.completed) {
        return left.completed ? 1 : -1;
      }

      return new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime();
    });
  }, [tasks]);

  const openTaskCount = useMemo(() => {
    return tasks.filter((task) => !task.completed).length;
  }, [tasks]);

  function handleAddTask(event) {
    event.preventDefault();
    const trimmedTask = taskText.trim();

    if (!trimmedTask) {
      return;
    }

    setTasks((current) => [
      {
        id: crypto.randomUUID(),
        text: trimmedTask,
        priority,
        completed: false,
        createdAt: new Date().toISOString(),
      },
      ...current,
    ]);
    setTaskText("");
    setPriority("Medium");
  }

  function toggleTask(taskId) {
    setTasks((current) =>
      current.map((task) =>
        task.id === taskId ? { ...task, completed: !task.completed } : task
      )
    );
  }

  function deleteTask(taskId) {
    setTasks((current) => current.filter((task) => task.id !== taskId));
  }

  return (
    <div className="notification-shell" ref={containerRef}>
      <button
        type="button"
        className={`notification-trigger${isOpen ? " open" : ""}`}
        aria-label="Tasks"
        aria-expanded={isOpen}
        aria-haspopup="dialog"
        onClick={() => setIsOpen((current) => !current)}
      >
        <ChecklistIcon />
        {openTaskCount > 0 && (
          <span className="notification-badge">{openTaskCount > 99 ? "99+" : openTaskCount}</span>
        )}
      </button>

      <div className={`notification-dropdown${isOpen ? " visible" : ""} task-dropdown`} role="dialog" aria-label="Tasks">
        <div className="notification-dropdown-header">
          <div>
            <strong>Task Manager</strong>
            <p>Track internal actions and follow-ups</p>
          </div>
          {openTaskCount > 0 && <span className="status-chip warning">{openTaskCount} open</span>}
        </div>

        <div className="notification-dropdown-body">
          <form className="task-form" onSubmit={handleAddTask}>
            <input
              value={taskText}
              onChange={(event) => setTaskText(event.target.value)}
              placeholder="Add new task"
            />
            <div className="task-form-row">
              <select value={priority} onChange={(event) => setPriority(event.target.value)}>
                <option value="High">High</option>
                <option value="Medium">Medium</option>
                <option value="Low">Low</option>
              </select>
              <button type="submit">Add</button>
            </div>
          </form>

          {sortedTasks.length === 0 ? (
            <p className="notification-state">No tasks yet</p>
          ) : (
            <div className="notification-list task-list">
              {sortedTasks.map((task) => (
                <div key={task.id} className={`task-item${task.completed ? " completed" : ""}`}>
                  <label className="task-main">
                    <input
                      type="checkbox"
                      checked={task.completed}
                      onChange={() => toggleTask(task.id)}
                    />
                    <span className="task-text">{task.text}</span>
                  </label>
                  <div className="task-meta">
                    <span className={`task-priority ${task.priority.toLowerCase()}`}>{task.priority}</span>
                    <button
                      type="button"
                      className="task-delete"
                      onClick={() => deleteTask(task.id)}
                      aria-label={`Delete ${task.text}`}
                    >
                      Delete
                    </button>
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

export default TaskDropdown;