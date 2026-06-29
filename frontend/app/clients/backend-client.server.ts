/** Builds a FormData body from a list of [name, value] entries. */
function form(...entries: [string, string | Blob, string?][]): FormData {
    const data = new FormData();
    for (const [name, value, filename] of entries) {
        if (filename !== undefined) data.append(name, value as Blob, filename);
        else data.append(name, value);
    }
    return data;
}

/**
 * Single entry point for every backend call: prepends BACKEND_URL, attaches the
 * shared api key, and converts a non-2xx response into an Error whose message is
 * prefixed with `errorPrefix` and suffixed with the backend's reported error.
 */
async function call(path: string, errorPrefix: string, init?: RequestInit): Promise<any> {
    const response = await fetch(process.env.BACKEND_URL + path, {
        ...init,
        headers: {
            "x-api-key": process.env.FRONTEND_BACKEND_API_KEY || "",
            ...(init?.headers ?? {}),
        },
    });

    if (!response.ok) {
        throw new Error(`${errorPrefix}: ${(await response.json()).error}`);
    }

    return response.json();
}

class BackendClient {
    public async isOnboarding(): Promise<boolean> {
        const data = await call("/api/is-onboarding", "Failed to fetch onboarding status", {
            method: "GET",
            headers: { "Content-Type": "application/json" },
        });
        return data.isOnboarding;
    }

    public async createAccount(username: string, password: string): Promise<boolean> {
        const data = await call("/api/create-account", "Failed to create account", {
            method: "POST",
            body: form(["username", username], ["password", password], ["type", "admin"]),
        });
        return data.status;
    }

    public async authenticate(username: string, password: string): Promise<boolean> {
        const data = await call("/api/authenticate", "Failed to authenticate", {
            method: "POST",
            body: form(["username", username], ["password", password], ["type", "admin"]),
        });
        return data.authenticated;
    }

    public async getQueue(limit: number): Promise<QueueResponse> {
        const data = await call(`/api?mode=queue&limit=${limit}`, "Failed to get queue");
        return data.queue;
    }

    public async getHistory(limit: number): Promise<HistoryResponse> {
        const data = await call(`/api?mode=history&pageSize=${limit}`, "Failed to get history");
        return data.history;
    }

    public async addNzb(nzbFile: File): Promise<string> {
        var config = await this.getConfig(["api.manual-category"]);
        var category = config.find(item => item.configName === "api.manual-category")?.configValue || "uncategorized";
        const data = await call(`/api?mode=addfile&cat=${category}&priority=0&pp=0`, "Failed to add nzb file", {
            method: "POST",
            body: form(["nzbFile", nzbFile, nzbFile.name]),
        });
        if (!data.nzo_ids || data.nzo_ids.length != 1) {
            throw new Error(`Failed to add nzb file: unexpected response format`);
        }
        return data.nzo_ids[0];
    }

    public async listWebdavDirectory(directory: string): Promise<DirectoryItem[]> {
        const data = await call("/api/list-webdav-directory", "Failed to list webdav directory", {
            method: "POST",
            body: form(["directory", directory]),
        });
        return data.items;
    }

    public async getConfig(keys: string[]): Promise<ConfigItem[]> {
        const data = await call("/api/get-config", "Failed to get config items", {
            method: "POST",
            body: form(...keys.map(key => ["config-keys", key] as [string, string])),
        });
        return data.configItems || [];
    }

    public async updateConfig(configItems: ConfigItem[]): Promise<boolean> {
        const data = await call("/api/update-config", "Failed to update config items", {
            method: "POST",
            body: form(...configItems.map(item => [item.configName, item.configValue] as [string, string])),
        });
        return data.status;
    }

    public async getHealthCheckQueue(pageSize?: number): Promise<HealthCheckQueueResponse> {
        const query = pageSize !== undefined ? `?pageSize=${pageSize}` : "";
        return await call(`/api/get-health-check-queue${query}`, "Failed to get health check queue", {
            method: "GET",
        });
    }

    public async getHealthCheckHistory(pageSize?: number): Promise<HealthCheckHistoryResponse> {
        const query = pageSize !== undefined ? `?pageSize=${pageSize}` : "";
        return await call(`/api/get-health-check-history${query}`, "Failed to get health check history", {
            method: "GET",
        });
    }
}

export const backendClient = new BackendClient();

export type QueueResponse = {
    slots: QueueSlot[],
    noofslots: number,
}

export type QueueSlot = {
    nzo_id: string,
    priority: string,
    filename: string,
    cat: string,
    percentage: string,
    true_percentage: string,
    status: string,
    mb: string,
    mbleft: string,
}

export type HistoryResponse = {
    slots: HistorySlot[],
    noofslots: number,
}

export type HistorySlot = {
    nzo_id: string,
    nzb_name: string,
    name: string,
    category: string,
    status: string,
    bytes: number,
    storage: string,
    download_time: number,
    fail_message: string,
    nzb_blob_id?: string,
}

export type DirectoryItem = {
    name: string,
    isDirectory: boolean,
    size: number | null | undefined,
    nzbBlobId?: string,
}

export type ConfigItem = {
    configName: string,
    configValue: string,
}

export type TestUsenetConnectionRequest = {
    host: string,
    port: string,
    useSsl: string,
    user: string,
    pass: string
}

export type HealthCheckQueueResponse = {
    uncheckedCount: number,
    items: HealthCheckQueueItem[]
}

export type HealthCheckQueueItem = {
    id: string,
    name: string,
    path: string,
    releaseDate: string | null,
    lastHealthCheck: string | null,
    nextHealthCheck: string | null,
    progress: number,
}

export type HealthCheckHistoryResponse = {
    stats: HealthCheckStats[],
    items: HealthCheckResult[]
}

export type HealthCheckStats = {
    result: HealthResult,
    repairStatus: RepairAction,
    count: number
}

export type HealthCheckResult = {
    id: string,
    createdAt: string,
    davItemId: string,
    path: string,
    result: HealthResult,
    repairStatus: RepairAction,
    message: string | null
}

export enum HealthResult {
    Healthy = 0,
    Unhealthy = 1,
}

export enum RepairAction {
    None = 0,
    Repaired = 1,
    Deleted = 2,
    ActionNeeded = 3,
}