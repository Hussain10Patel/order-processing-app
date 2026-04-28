import { NavLink } from "react-router-dom";
import NotificationDropdown from "./NotificationDropdown";
import TaskDropdown from "./TaskDropdown";

const links = [
  { to: "/dashboard", label: "Dashboard" },
  { to: "/upload", label: "Upload CSV" },
  { to: "/production", label: "Production" },
  { to: "/delivery", label: "Delivery" },
  { to: "/reports", label: "Reports" },
  { to: "/admin", label: "Admin" },
];

function Layout({ children }) {
  return (
    <div className="app-shell">
      <aside className="sidebar">
        <h1>Order Processing</h1>
        <nav>
          {links.map((link) => (
            <NavLink
              key={link.to}
              to={link.to}
              className={({ isActive }) =>
                isActive ? "nav-link active" : "nav-link"
              }
            >
              {link.label}
            </NavLink>
          ))}
        </nav>
      </aside>
      <main className="content">
        <header className="content-header">
          <div className="content-header-copy">
            <span>Operations Workspace</span>
          </div>
          <div className="header-actions">
            <TaskDropdown />
            <NotificationDropdown />
          </div>
        </header>
        {children}
      </main>
    </div>
  );
}

export default Layout;
