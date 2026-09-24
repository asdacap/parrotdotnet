import { createClient, type Interceptor } from "@connectrpc/connect"
import { createGrpcWebTransport } from "@connectrpc/connect-web"

import { Parrot } from "@/gen/parrot_pb"
import { ParrotWeb, type DescribeHostResponse } from "@/gen/web_pb"

const tokenStorageKey = "parrot.token"

function takeToken(): string | null {
  const tokenFromHash = new URLSearchParams(location.hash.slice(1)).get("token")
  if (tokenFromHash) {
    sessionStorage.setItem(tokenStorageKey, tokenFromHash)
    history.replaceState(null, "", `${location.pathname}${location.search}#/`)
  }
  return sessionStorage.getItem(tokenStorageKey)
}

export const token = takeToken()

const authorize: Interceptor = (next) => (request) => {
  if (token) request.header.set("Authorization", `Bearer ${token}`)
  return next(request)
}

const transport = createGrpcWebTransport({
  baseUrl: location.origin,
  useBinaryFormat: true,
  interceptors: [authorize],
})

export const parrot = createClient(Parrot, transport)
export const parrotWeb = createClient(ParrotWeb, transport)

let hostDescription: Promise<DescribeHostResponse> | undefined

export function describeHost(): Promise<DescribeHostResponse> {
  hostDescription ??= parrotWeb.describeHost({})
  return hostDescription
}
