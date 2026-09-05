import React from "react";
import { createRoot } from "react-dom/client";
import App from "./Knowledge";
import "./styles.css";
createRoot(document.getElementById("root")!).render(
    <React.StrictMode>
        <App />
    </React.StrictMode>,
);
