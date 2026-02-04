/**
 * Simple router for Cloudflare Workers
 */

type RouteHandler = (request: Request, params: Record<string, string>) => Promise<Response> | Response;

interface Route {
  method: string;
  pattern: RegExp;
  paramNames: string[];
  handler: RouteHandler;
}

export class Router {
  private routes: Route[] = [];

  private addRoute(method: string, path: string, handler: RouteHandler): void {
    const paramNames: string[] = [];
    
    // Convert path pattern to regex
    // /video/:id -> /video/([^/]+)
    // /proxy/* -> /proxy/(.*)
    const pattern = path
      .replace(/\//g, '\\/')
      .replace(/:([^/]+)/g, (_, name) => {
        paramNames.push(name);
        return '([^/]+)';
      })
      .replace(/\*/g, () => {
        paramNames.push('wildcard');
        return '(.*)';
      });

    this.routes.push({
      method,
      pattern: new RegExp(`^${pattern}$`),
      paramNames,
      handler,
    });
  }

  get(path: string, handler: RouteHandler): void {
    this.addRoute('GET', path, handler);
  }

  post(path: string, handler: RouteHandler): void {
    this.addRoute('POST', path, handler);
  }

  put(path: string, handler: RouteHandler): void {
    this.addRoute('PUT', path, handler);
  }

  delete(path: string, handler: RouteHandler): void {
    this.addRoute('DELETE', path, handler);
  }

  async handle(request: Request): Promise<Response> {
    const url = new URL(request.url);
    const path = url.pathname;
    const method = request.method;

    for (const route of this.routes) {
      if (route.method !== method) continue;

      const match = path.match(route.pattern);
      if (match) {
        const params: Record<string, string> = {};
        route.paramNames.forEach((name, index) => {
          params[name] = match[index + 1];
        });
        return route.handler(request, params);
      }
    }

    return new Response('Not Found', { status: 404 });
  }
}
